using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using LaunchFlow.Models;
using Microsoft.Data.Sqlite;

namespace LaunchFlow.Services;

/// <summary>
/// Encrypted SQLite vault at %LOCALAPPDATA%\LaunchFlow\launchflow.db.
/// Workflows and each site password are AES-256-GCM sealed blobs keyed by Argon2id.
/// The SQLite container itself is not SQLCipher; ciphertext lives in BLOB columns.
/// </summary>
public sealed class VaultDatabase
{
    public static VaultDatabase Current { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    // Stronger defaults for NEW vaults. Existing vaults keep the params stored in vault_meta.
    private const int ArgonMemoryKb = 128 * 1024; // 128 MiB
    private const int ArgonIterations = 4;
    private const int ArgonLanes = 2;
    private const int SchemaVersion = 2;

    private readonly object _gate = new();
    private byte[]? _key;
    private Configuration? _configCache;
    private Dictionary<Guid, SiteCredential>? _secretsCache;

    public string DatabasePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchFlow", "launchflow.db");

    public string DataDirectory =>
        Path.GetDirectoryName(DatabasePath)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchFlow");

    public bool Exists => File.Exists(DatabasePath);

    public bool IsUnlocked
    {
        get { lock (_gate) return _key is not null; }
    }

    public bool Unlock(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        lock (_gate)
        {
            EnsureSchema();
            var meta = ReadMeta();
            var key = DeriveKey(password, meta.Salt, meta.MemoryKb, meta.Iterations, meta.Lanes);
            try
            {
                if (!TryDecrypt(meta.Verifier, key, out var plain) ||
                    Encoding.UTF8.GetString(plain) != "LaunchFlow.Vault.v1")
                {
                    CryptographicOperations.ZeroMemory(key);
                    return false;
                }

                ZeroKey();
                _key = key;
                key = null;
                _configCache = null;
                _secretsCache = null;
                MigrateLegacyCredentialBlobIfNeeded();
                return true;
            }
            catch
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                return false;
            }
        }
    }

    public void Initialize(string password, Configuration config, Dictionary<Guid, SiteCredential>? secrets = null)
    {
        if (!PasswordStrength.MeetsMinimum(password))
            throw new ArgumentException(
                $"Master password must be at least {PasswordStrength.MinimumLength} characters and not trivially weak.");

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            if (File.Exists(DatabasePath))
                throw new InvalidOperationException("Vault database already exists.");

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var key = DeriveKey(password, salt, ArgonMemoryKb, ArgonIterations, ArgonLanes);
            try
            {
                using var connection = OpenConnection(create: true);
                EnsureSchema(connection);
                var verifier = Encrypt(Encoding.UTF8.GetBytes("LaunchFlow.Vault.v1"), key);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText =
                        "INSERT INTO vault_meta (id, salt, verifier, argon_memory_kb, argon_iterations, argon_lanes, schema_version) " +
                        "VALUES (1, $salt, $verifier, $mem, $iter, $lanes, $ver);";
                    cmd.Parameters.AddWithValue("$salt", salt);
                    cmd.Parameters.AddWithValue("$verifier", verifier);
                    cmd.Parameters.AddWithValue("$mem", ArgonMemoryKb);
                    cmd.Parameters.AddWithValue("$iter", ArgonIterations);
                    cmd.Parameters.AddWithValue("$lanes", ArgonLanes);
                    cmd.Parameters.AddWithValue("$ver", SchemaVersion);
                    cmd.ExecuteNonQuery();
                }

                _key = key;
                key = null;
                WriteSealed(connection, "config", Serialize(config));
                PersistCredentials(connection, secrets ?? new Dictionary<Guid, SiteCredential>());
                _configCache = CloneConfig(config);
                _secretsCache = CloneSecrets(secrets ?? new Dictionary<Guid, SiteCredential>());
            }
            finally
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
            }
        }

    }

    public void Lock()
    {
        lock (_gate)
        {
            ZeroKey();
            _configCache = null;
            _secretsCache = null;
        }
    }

    public void ReplaceSnapshot(Configuration config, Dictionary<Guid, SiteCredential> credentials)
    {
        RequireUnlocked();
        lock (_gate)
        {
            foreach (var w in config.Workflows)
                w.Enabled = false;

            using var connection = OpenConnection();
            using var tx = connection.BeginTransaction();
            WriteSealed(connection, "config", Serialize(config));
            PersistCredentials(connection, credentials);
            tx.Commit();
            _configCache = CloneConfig(config);
            _secretsCache = CloneSecrets(credentials);
        }
    }

    public Configuration LoadConfiguration()
    {
        RequireUnlocked();
        lock (_gate)
        {
            if (_configCache is not null) return CloneConfig(_configCache);
            using var connection = OpenConnection();
            var bytes = ReadSealed(connection, "config");
            var config = Deserialize<Configuration>(bytes) ?? new Configuration();
            _configCache = CloneConfig(config);
            return CloneConfig(config);
        }
    }

    public void SaveConfiguration(Configuration config)
    {
        RequireUnlocked();
        lock (_gate)
        {
            using var connection = OpenConnection();
            WriteSealed(connection, "config", Serialize(config));
            _configCache = CloneConfig(config);
        }
    }

    public bool HasCredential(Guid id)
    {
        RequireUnlocked();
        lock (_gate)
        {
            return LoadSecretsUnlocked().ContainsKey(id);
        }
    }

    public SiteCredential? ReadCredential(Guid id)
    {
        RequireUnlocked();
        lock (_gate)
        {
            return LoadSecretsUnlocked().TryGetValue(id, out var cred)
                ? cred with { }
                : null;
        }
    }

    public void SaveCredential(Guid id, SiteCredential credential)
    {
        RequireUnlocked();
        lock (_gate)
        {
            var secrets = LoadSecretsUnlocked();
            secrets[id] = credential;
            using var connection = OpenConnection();
            PersistCredentials(connection, secrets);
            _secretsCache = CloneSecrets(secrets);
        }
    }

    public void DeleteCredential(Guid id)
    {
        RequireUnlocked();
        lock (_gate)
        {
            var secrets = LoadSecretsUnlocked();
            if (!secrets.Remove(id)) return;
            using var connection = OpenConnection();
            PersistCredentials(connection, secrets);
            _secretsCache = CloneSecrets(secrets);
        }
    }

    public Dictionary<Guid, SiteCredential> ExportCredentials()
    {
        RequireUnlocked();
        lock (_gate)
        {
            return CloneSecrets(LoadSecretsUnlocked());
        }
    }

    public void ChangeMasterPassword(string currentPassword, string newPassword)
    {
        if (!PasswordStrength.MeetsMinimum(newPassword))
            throw new ArgumentException(
                $"New master password must be at least {PasswordStrength.MinimumLength} characters and not trivially weak.");

        lock (_gate)
        {
            // Verify current password without discarding a good session on failure.
            var meta = ReadMeta();
            var probe = DeriveKey(currentPassword, meta.Salt, meta.MemoryKb, meta.Iterations, meta.Lanes);
            try
            {
                if (!TryDecrypt(meta.Verifier, probe, out var plain) ||
                    Encoding.UTF8.GetString(plain) != "LaunchFlow.Vault.v1")
                    throw new InvalidOperationException("Current master password is incorrect.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(probe);
            }

            if (_key is null)
            {
                if (!Unlock(currentPassword))
                    throw new InvalidOperationException("Current master password is incorrect.");
            }

            var config = LoadConfigurationUnlocked();
            var secrets = CloneSecrets(LoadSecretsUnlocked());
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var newKey = DeriveKey(newPassword, salt, ArgonMemoryKb, ArgonIterations, ArgonLanes);
            try
            {
                using var connection = OpenConnection();
                using var tx = connection.BeginTransaction();
                var verifier = Encrypt(Encoding.UTF8.GetBytes("LaunchFlow.Vault.v1"), newKey);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText =
                        "UPDATE vault_meta SET salt=$salt, verifier=$verifier, argon_memory_kb=$mem, " +
                        "argon_iterations=$iter, argon_lanes=$lanes, schema_version=$ver WHERE id=1;";
                    cmd.Parameters.AddWithValue("$salt", salt);
                    cmd.Parameters.AddWithValue("$verifier", verifier);
                    cmd.Parameters.AddWithValue("$mem", ArgonMemoryKb);
                    cmd.Parameters.AddWithValue("$iter", ArgonIterations);
                    cmd.Parameters.AddWithValue("$lanes", ArgonLanes);
                    cmd.Parameters.AddWithValue("$ver", SchemaVersion);
                    cmd.ExecuteNonQuery();
                }

                ZeroKey();
                _key = newKey;
                newKey = null;
                WriteSealed(connection, "config", Serialize(config));
                PersistCredentials(connection, secrets);
                tx.Commit();
                _configCache = CloneConfig(config);
                _secretsCache = CloneSecrets(secrets);
            }
            finally
            {
                if (newKey is not null) CryptographicOperations.ZeroMemory(newKey);
            }
        }
    }

    private Configuration LoadConfigurationUnlocked()
    {
        if (_configCache is not null) return CloneConfig(_configCache);
        using var connection = OpenConnection();
        var bytes = ReadSealed(connection, "config");
        var config = Deserialize<Configuration>(bytes) ?? new Configuration();
        _configCache = CloneConfig(config);
        return CloneConfig(config);
    }

    private Dictionary<Guid, SiteCredential> LoadSecretsUnlocked()
    {
        if (_secretsCache is not null) return _secretsCache;
        using var connection = OpenConnection();
        MigrateLegacyCredentialBlobIfNeeded(connection);
        _secretsCache = ReadCredentialRows(connection);
        return _secretsCache;
    }

    private Dictionary<Guid, SiteCredential> ReadCredentialRows(SqliteConnection connection)
    {
        var result = new Dictionary<Guid, SiteCredential>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT workflow_id, sealed FROM credentials;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var idText = reader.GetString(0);
            if (!Guid.TryParse(idText, out var id)) continue;
            var sealedBytes = (byte[])reader["sealed"];
            if (!TryDecrypt(sealedBytes, _key!, out var plain))
                throw new CryptographicException("Unable to decrypt a stored credential.");
            var cred = Deserialize<SiteCredential>(plain)
                       ?? throw new InvalidOperationException("Credential payload was empty.");
            result[id] = cred;
        }
        return result;
    }

    private void PersistCredentials(SqliteConnection connection, Dictionary<Guid, SiteCredential> secrets)
    {
        if (_key is null) throw new InvalidOperationException("Vault is locked.");
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM credentials;";
            clear.ExecuteNonQuery();
        }

        foreach (var (id, cred) in secrets)
        {
            var sealedBytes = Encrypt(Serialize(cred), _key);
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO credentials (workflow_id, sealed) VALUES ($id, $sealed);";
            cmd.Parameters.AddWithValue("$id", id.ToString("N"));
            cmd.Parameters.AddWithValue("$sealed", sealedBytes);
            cmd.ExecuteNonQuery();
        }

        // Remove legacy single-blob credentials object if present.
        using var drop = connection.CreateCommand();
        drop.CommandText = "DELETE FROM objects WHERE id='credentials';";
        drop.ExecuteNonQuery();
    }

    private void MigrateLegacyCredentialBlobIfNeeded(SqliteConnection? existing = null)
    {
        if (_key is null) return;
        var owns = existing is null;
        var connection = existing ?? OpenConnection();
        try
        {
            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM credentials;";
                var count = Convert.ToInt64(countCmd.ExecuteScalar() ?? 0L);
                if (count > 0) return;
            }

            using var blobCmd = connection.CreateCommand();
            blobCmd.CommandText = "SELECT sealed FROM objects WHERE id='credentials';";
            var blob = blobCmd.ExecuteScalar() as byte[];
            if (blob is null) return;

            if (!TryDecrypt(blob, _key, out var plain))
                throw new CryptographicException("Unable to migrate legacy credential blob.");
            var secrets = Deserialize<Dictionary<Guid, SiteCredential>>(plain)
                          ?? new Dictionary<Guid, SiteCredential>();
            using var tx = connection.BeginTransaction();
            PersistCredentials(connection, secrets);
            using (var ver = connection.CreateCommand())
            {
                ver.CommandText = "UPDATE vault_meta SET schema_version=$ver WHERE id=1;";
                ver.Parameters.AddWithValue("$ver", SchemaVersion);
                ver.ExecuteNonQuery();
            }
            tx.Commit();
            _secretsCache = CloneSecrets(secrets);
        }
        finally
        {
            if (owns) connection.Dispose();
        }
    }

    private void RequireUnlocked()
    {
        if (!IsUnlocked)
            throw new InvalidOperationException("Encrypted vault is locked. Enter the master password first.");
    }

    private void ZeroKey()
    {
        if (_key is null) return;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    private SqliteConnection OpenConnection(bool create = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite
        }.ToString());
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON;";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }

    private void EnsureSchema(SqliteConnection? existing = null)
    {
        var owns = existing is null;
        var connection = existing ?? OpenConnection(create: true);
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS vault_meta (
                      id INTEGER PRIMARY KEY CHECK (id = 1),
                      salt BLOB NOT NULL,
                      verifier BLOB NOT NULL,
                      argon_memory_kb INTEGER NOT NULL,
                      argon_iterations INTEGER NOT NULL,
                      argon_lanes INTEGER NOT NULL,
                      schema_version INTEGER NOT NULL DEFAULT 1
                    );
                    CREATE TABLE IF NOT EXISTS objects (
                      id TEXT PRIMARY KEY NOT NULL,
                      sealed BLOB NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS credentials (
                      workflow_id TEXT PRIMARY KEY NOT NULL,
                      sealed BLOB NOT NULL
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            EnsureColumn(connection, "vault_meta", "schema_version", "INTEGER NOT NULL DEFAULT 1");
        }
        finally
        {
            if (owns) connection.Dispose();
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string decl)
    {
        using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table});";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader["name"]?.ToString(), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {decl};";
        alter.ExecuteNonQuery();
    }

    private sealed record VaultMeta(byte[] Salt, byte[] Verifier, int MemoryKb, int Iterations, int Lanes);

    private VaultMeta ReadMeta()
    {
        using var connection = OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT salt, verifier, argon_memory_kb, argon_iterations, argon_lanes FROM vault_meta WHERE id=1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("Vault metadata is missing.");
        return new VaultMeta(
            (byte[])reader["salt"],
            (byte[])reader["verifier"],
            Convert.ToInt32(reader["argon_memory_kb"]),
            Convert.ToInt32(reader["argon_iterations"]),
            Convert.ToInt32(reader["argon_lanes"]));
    }

    private void WriteSealed(SqliteConnection connection, string id, byte[] plaintext)
    {
        if (_key is null) throw new InvalidOperationException("Vault is locked.");
        var sealedBytes = Encrypt(plaintext, _key);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO objects (id, sealed) VALUES ($id, $sealed)
            ON CONFLICT(id) DO UPDATE SET sealed=excluded.sealed;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sealed", sealedBytes);
        cmd.ExecuteNonQuery();
    }

    private byte[] ReadSealed(SqliteConnection connection, string id)
    {
        if (_key is null) throw new InvalidOperationException("Vault is locked.");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sealed FROM objects WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        var result = cmd.ExecuteScalar() as byte[]
                     ?? throw new InvalidOperationException($"Vault object '{id}' is missing.");
        if (!TryDecrypt(result, _key, out var plain))
            throw new CryptographicException("Unable to decrypt vault object.");
        return plain;
    }

    private static byte[] DeriveKey(string password, byte[] salt, int memoryKb, int iterations, int lanes)
    {
        var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = Math.Clamp(lanes, 1, 8),
            MemorySize = Math.Clamp(memoryKb, 16 * 1024, 512 * 1024),
            Iterations = Math.Clamp(iterations, 1, 12)
        };
        return argon.GetBytes(KeySize);
    }

    private static byte[] Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var output = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, output, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, output, NonceSize + ciphertext.Length, TagSize);
        return output;
    }

    private static bool TryDecrypt(byte[] sealedBytes, byte[] key, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        if (sealedBytes.Length < NonceSize + TagSize) return false;
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        var tag = sealedBytes.AsSpan(sealedBytes.Length - TagSize, TagSize);
        var ciphertext = sealedBytes.AsSpan(NonceSize, sealedBytes.Length - NonceSize - TagSize);
        plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] Serialize<T>(T value) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));

    private static T? Deserialize<T>(byte[] bytes) =>
        JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(bytes), JsonOptions);

    private static Configuration CloneConfig(Configuration source) =>
        Deserialize<Configuration>(Serialize(source))!;

    private static Dictionary<Guid, SiteCredential> CloneSecrets(Dictionary<Guid, SiteCredential> source) =>
        source.ToDictionary(kv => kv.Key, kv => kv.Value with { });
}
