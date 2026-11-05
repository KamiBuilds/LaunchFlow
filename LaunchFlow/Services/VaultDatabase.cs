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
/// <summary>Structural disk state only; authorization and decryption require Unlock.</summary>
public enum VaultStorageState
{
    Absent,
    PresentNeedsAuthentication,
    LegacyUpgradeRequired,
    IncompleteOrUnsupported
}

public sealed partial class VaultDatabase
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
    // Monotonic vault authorization identity; read and write under _gate.
    private long _sessionGeneration;
    public long SessionGeneration
    {
        get { lock (_gate) return _sessionGeneration; }
    }
    private Configuration? _configCache;
    private Dictionary<Guid, SiteCredential>? _secretsCache;

    // Production uses the exact original location. An explicit synthetic
    // directory can be used by test harnesses without touching user data.
    public VaultDatabase(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchFlow", "launchflow.db");
    }

    public string DatabasePath { get; }

    public string DataDirectory =>
        Path.GetDirectoryName(DatabasePath)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchFlow");

    public bool Exists => File.Exists(DatabasePath);

    /// <summary>
    /// Classify an existing vault without creating tables, changing metadata, or
    /// attempting to decrypt records. A structurally recognized file still needs
    /// the user's master password and is not a claim of healthy credentials.
    /// </summary>
    public VaultStorageState InspectStorage()
    {
        lock (_gate)
        {
            if (!File.Exists(DatabasePath)) return VaultStorageState.Absent;
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = DatabasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
                connection.Open();

                using (var integrity = connection.CreateCommand())
                {
                    integrity.CommandText = "PRAGMA quick_check(1);";
                    if (!string.Equals(integrity.ExecuteScalar()?.ToString(), "ok",
                            StringComparison.OrdinalIgnoreCase))
                        return VaultStorageState.IncompleteOrUnsupported;
                }

                if (TableExists(connection, "secure_meta"))
                    return InspectStrictStructure(connection);
                if (TableExists(connection, "secure_objects") ||
                    TableExists(connection, "secure_credentials"))
                    return VaultStorageState.IncompleteOrUnsupported;

                if (!HasColumns(connection, "vault_meta", "id", "salt", "verifier",
                        "argon_memory_kb", "argon_iterations", "argon_lanes") ||
                    !HasColumns(connection, "objects", "id", "sealed"))
                    return VaultStorageState.IncompleteOrUnsupported;

                var hasSchemaVersion = HasColumns(connection, "vault_meta", "schema_version");
                var detectedSchema = 1;
                using (var metadata = connection.CreateCommand())
                {
                    metadata.CommandText = hasSchemaVersion
                        ? "SELECT salt, verifier, argon_memory_kb, argon_iterations, argon_lanes, " +
                          "schema_version FROM vault_meta WHERE id=1;"
                        : "SELECT salt, verifier, argon_memory_kb, argon_iterations, argon_lanes " +
                          "FROM vault_meta WHERE id=1;";
                    using var reader = metadata.ExecuteReader();
                    if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
                        return VaultStorageState.IncompleteOrUnsupported;
                    var salt = reader.GetFieldValue<byte[]>(0);
                    var verifier = reader.GetFieldValue<byte[]>(1);
                    var memory = reader.GetInt32(2);
                    var passes = reader.GetInt32(3);
                    var lanes = reader.GetInt32(4);
                    detectedSchema = hasSchemaVersion ? reader.GetInt32(5) : 1;
                    if (salt.Length != SaltSize ||
                        verifier.Length < NonceSize + TagSize ||
                        memory < 8 || memory > 1024 * 1024 ||
                        passes < 1 || passes > 1000 || lanes < 1 || lanes > 64 ||
                        detectedSchema < 1 || detectedSchema > SchemaVersion)
                        return VaultStorageState.IncompleteOrUnsupported;
                }

                using var configuration = connection.CreateCommand();
                configuration.CommandText = "SELECT sealed FROM objects WHERE id='config';";
                var configBytes = configuration.ExecuteScalar() as byte[];
                if (configBytes is null || configBytes.Length < NonceSize + TagSize)
                    return VaultStorageState.IncompleteOrUnsupported;

                // Older vaults may hold their credentials in objects instead of
                // a dedicated table; the unlock path handles that migration.
                if (HasColumns(connection, "credentials", "workflow_id", "sealed"))
                {
                    // The table exists with its expected shape; decryption is
                    // deliberately deferred until the authorized unlock.
                }
                else if (detectedSchema >= 2 || TableExists(connection, "credentials"))
                {
                    return VaultStorageState.IncompleteOrUnsupported;
                }

                return VaultStorageState.LegacyUpgradeRequired;
            }
            catch (Exception ex) when (ex is SqliteException or IOException or
                UnauthorizedAccessException or InvalidOperationException or
                FormatException or InvalidCastException or OverflowException)
            {
                return VaultStorageState.IncompleteOrUnsupported;
            }
        }
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table;";
        cmd.Parameters.AddWithValue("$table", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool HasColumns(SqliteConnection connection, string table, params string[] expected)
    {
        if (!TableExists(connection, table)) return false;
        using var cmd = connection.CreateCommand();
        // Table identifiers below are fixed internal constants, not user input.
        cmd.CommandText = "PRAGMA table_info(" + table + ");";
        using var reader = cmd.ExecuteReader();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) found.Add(reader.GetString(1));
        return expected.All(found.Contains);
    }

    public bool IsUnlocked
    {
        get { lock (_gate) return _key is not null; }
    }

    public bool Unlock(string password) => UnlockStrict(password);

    public void Initialize(string password, Configuration config,
        Dictionary<Guid, SiteCredential>? secrets = null) =>
        InitializeStrict(password, config, secrets);

    public void Lock()
    {
        // Revoke an in-flight private legacy-upgrade candidate even before it
        // has published a normal session key.
        System.Threading.Interlocked.Increment(ref _upgradeCancelEpoch);
        lock (_gate)
        {
            ZeroKey();
            _configCache = null;
            _secretsCache = null;
        }
    }

    public void ReplaceSnapshot(Configuration config, Dictionary<Guid, SiteCredential> credentials) =>
        ReplaceSnapshotUnderGate(config, credentials, expectedSession: null);

    public void ReplaceSnapshot(Configuration config, Dictionary<Guid, SiteCredential> credentials,
        long expectedSession) =>
        ReplaceSnapshotUnderGate(config, credentials, expectedSession);

    private void ReplaceSnapshotUnderGate(Configuration config,
        Dictionary<Guid, SiteCredential> credentials, long? expectedSession)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(credentials);
        var policy = EnterprisePolicy.Current;
        policy.RequireTransfer();
        // An empty replacement removes saved credentials, so storage permission is mandatory.
        policy.RequireCredentialStorage();
        lock (_gate)
        {
            if (_key is null || (expectedSession.HasValue && _sessionGeneration != expectedSession.Value))
                throw new InvalidOperationException("Vault authorization changed; import was not applied.");

            var candidateConfig = CloneConfig(config);
            foreach (var workflow in candidateConfig.Workflows)
                workflow.Enabled = false;
            var candidateCredentials = CloneSecrets(credentials);

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            WriteSealed(connection, "config", Serialize(candidateConfig));
            PersistCredentials(connection, candidateCredentials);
            transaction.Commit();

            foreach (var workflow in config.Workflows)
                workflow.Enabled = false;
            _configCache = candidateConfig;
            _secretsCache = candidateCredentials;
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
            var config = Deserialize<Configuration>(bytes)
            ?? throw new InvalidDataException("Vault configuration is empty.");
            _configCache = CloneConfig(config);
            return CloneConfig(config);
        }
    }

    public void SaveConfiguration(Configuration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate) SaveConfigurationUnderGate(config, expectedSession: null);
    }

    /// <summary>
    /// Commit only while the exact captured vault authorization is current.
    /// The session/key checks and encrypted write share the SAME lock.
    /// </summary>
    public void SaveConfiguration(Configuration config, long expectedSession)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate) SaveConfigurationUnderGate(config, expectedSession);
    }

    private void SaveConfigurationUnderGate(Configuration config, long? expectedSession)
    {
        if (_key is null || (expectedSession.HasValue && _sessionGeneration != expectedSession))
            throw new InvalidOperationException("Vault authorization changed; configuration was not saved.");
        using var connection = OpenConnection();
        WriteSealed(connection, "config", Serialize(config));
        _configCache = CloneConfig(config);
    }

    public bool HasCredential(Guid id)
    {
        EnterprisePolicy.Current.RequireCredentialStorage();
        RequireUnlocked();
        lock (_gate)
        {
            return LoadSecretsUnlocked().ContainsKey(id);
        }
    }

    public SiteCredential? ReadCredential(Guid id)
    {
        EnterprisePolicy.Current.RequireCredentialRead();
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
        ArgumentNullException.ThrowIfNull(credential);
        if (id == Guid.Empty) throw new ArgumentException("A workflow ID is required.", nameof(id));
        lock (_gate)
        {
            EnterprisePolicy.Current.RequireCredentialStorage();
            if (_key is null) throw new InvalidOperationException("Encrypted vault is locked.");
            var candidate = CloneSecrets(LoadSecretsUnlocked());
            candidate[id] = credential;
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            PersistCredentials(connection, candidate);
            transaction.Commit();
            _secretsCache = candidate;
        }
    }

    public void SaveCredentialAndConfiguration(Guid id, SiteCredential credential,
        Configuration configuration, long expectedSession)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(configuration);
        if (id == Guid.Empty || configuration.Workflows.Count(w => w.Id == id) != 1)
            throw new InvalidOperationException("The selected workflow is not uniquely present.");

        lock (_gate)
        {
            EnterprisePolicy.Current.RequireCredentialStorage();
            if (_key is null || _sessionGeneration != expectedSession)
                throw new InvalidOperationException("Vault authorization changed before saving credentials.");

            var candidateConfig = CloneConfig(configuration);
            var candidateSecrets = CloneSecrets(LoadSecretsUnlocked());
            candidateSecrets[id] = credential;

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            PersistCredentials(connection, candidateSecrets);
            WriteSealed(connection, "config", Serialize(candidateConfig));
            transaction.Commit();
            _configCache = candidateConfig;
            _secretsCache = candidateSecrets;
        }
    }

    public void DeleteCredential(Guid id)
    {
        lock (_gate)
        {
            if (_key is null) throw new InvalidOperationException("Encrypted vault is locked.");
            var candidate = CloneSecrets(LoadSecretsUnlocked());
            if (!candidate.Remove(id)) return;
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            PersistCredentials(connection, candidate);
            transaction.Commit();
            _secretsCache = candidate;
        }
    }

    public Dictionary<Guid, SiteCredential> ExportCredentials()
    {
        var policy = EnterprisePolicy.Current;
        policy.RequireTransfer();
        lock (_gate)
        {
            policy.RequireCredentialStorage();
            if (_key is null) throw new InvalidOperationException("Unlock the vault before exporting credentials.");
            return CloneSecrets(LoadSecretsUnlocked());
        }
    }

    public (Configuration Config, Dictionary<Guid, SiteCredential> Credentials) CaptureExportSnapshot(
        bool includePasswords, long expectedSession)
    {
        var policy = EnterprisePolicy.Current;
        policy.RequireTransfer();
        lock (_gate)
        {
            if (_key is null || _sessionGeneration != expectedSession)
                throw new InvalidOperationException("Vault authorization changed before export. Unlock and retry.");
            if (includePasswords) policy.RequireCredentialStorage();
            var config = LoadConfigurationUnlocked();
            var credentials = includePasswords
                ? CloneSecrets(LoadSecretsUnlocked())
                : new Dictionary<Guid, SiteCredential>();
            return (config, credentials);
        }
    }

    public void ChangeMasterPassword(string currentPassword, string newPassword) =>
        ChangeMasterStrict(currentPassword, newPassword);

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
        _secretsCache = ReadCredentialRows(connection);
        return _secretsCache;
    }

    private Dictionary<Guid, SiteCredential> ReadCredentialRows(SqliteConnection connection,
        byte[]? operationKey = null, byte[]? operationVaultId = null)
    {
        var key = operationKey ?? _key ?? throw new InvalidOperationException("Vault is locked.");
        var vaultId = operationVaultId ?? _strictVaultId
            ?? throw new InvalidOperationException("Vault format context is missing.");
        var result = new Dictionary<Guid, SiteCredential>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT workflow_id, sealed FROM secure_credentials;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var idText = reader.GetString(0);
            if (!Guid.TryParseExact(idText, "N", out var id) ||
                id == Guid.Empty || id.ToString("N") != idText || result.ContainsKey(id))
                throw new InvalidDataException("Invalid or duplicate encrypted workflow identity.");
            var envelope = (byte[])reader["sealed"];
            var plain = VaultRecordCodec.Open(envelope, key, vaultId, "credential", idText);
            try
            {
                result.Add(id, Deserialize<SiteCredential>(plain)
                    ?? throw new InvalidDataException("Credential payload was empty."));
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return result;
    }

    private void PersistCredentials(SqliteConnection connection, Dictionary<Guid, SiteCredential> secrets,
        byte[]? operationKey = null, byte[]? operationVaultId = null)
    {
        var key = operationKey ?? _key ?? throw new InvalidOperationException("Vault is locked.");
        var vaultId = operationVaultId ?? _strictVaultId
            ?? throw new InvalidOperationException("Vault format context is missing.");
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM secure_credentials;";
            clear.ExecuteNonQuery();
        }
        foreach (var (id, credential) in secrets)
        {
            if (id == Guid.Empty)
                throw new InvalidDataException("Empty credential workflow identity.");
            var owner = id.ToString("N");
            var envelope = VaultRecordCodec.Seal(Serialize(credential), key, vaultId,
                "credential", owner);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO secure_credentials (workflow_id,sealed) VALUES ($id,$s);";
            cmd.Parameters.AddWithValue("$id", owner);
            cmd.Parameters.AddWithValue("$s", envelope);
            cmd.ExecuteNonQuery();
        }
    }

    private void MigrateLegacyCredentialBlobIfNeeded(SqliteConnection? existing = null,
        byte[]? operationKey = null)
    {
        var key = operationKey ?? _key;
        if (key is null) return;
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

            if (!TryDecrypt(blob, key, out var plain))
                throw new CryptographicException("Unable to migrate legacy credential blob.");
            var secrets = Deserialize<Dictionary<Guid, SiteCredential>>(plain)
                ?? throw new InvalidDataException("Legacy credential data was empty.");
            var candidateCache = CloneSecrets(secrets);
            using var tx = connection.BeginTransaction();
            PersistCredentials(connection, secrets, key);
            using (var ver = connection.CreateCommand())
            {
                ver.CommandText = "UPDATE vault_meta SET schema_version=$ver WHERE id=1;";
                ver.Parameters.AddWithValue("$ver", SchemaVersion);
                ver.ExecuteNonQuery();
            }
            tx.Commit();
            if (operationKey is null)
                _secretsCache = candidateCache;
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
        // Revoke the previous authorization before dropping the old key.
        _sessionGeneration++;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _strictVaultId = null;
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

    private void WriteSealed(SqliteConnection connection, string id, byte[] plaintext,
        byte[]? operationKey = null, byte[]? operationVaultId = null)
    {
        if (id != "config") throw new InvalidDataException("Unknown strict vault object.");
        var key = operationKey ?? _key ?? throw new InvalidOperationException("Vault is locked.");
        var vaultId = operationVaultId ?? _strictVaultId
            ?? throw new InvalidOperationException("Vault format context is missing.");
        var envelope = VaultRecordCodec.Seal(plaintext, key, vaultId, "config", "config");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO secure_objects (id,sealed) VALUES ($id,$s) " +
            "ON CONFLICT(id) DO UPDATE SET sealed=excluded.sealed;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$s", envelope);
        cmd.ExecuteNonQuery();
    }

    private byte[] ReadSealed(SqliteConnection connection, string id,
        byte[]? operationKey = null, byte[]? operationVaultId = null)
    {
        if (id != "config") throw new InvalidDataException("Unknown strict vault object.");
        var key = operationKey ?? _key ?? throw new InvalidOperationException("Vault is locked.");
        var vaultId = operationVaultId ?? _strictVaultId
            ?? throw new InvalidOperationException("Vault format context is missing.");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sealed FROM secure_objects WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        var envelope = cmd.ExecuteScalar() as byte[]
            ?? throw new InvalidDataException("Encrypted configuration is missing.");
        return VaultRecordCodec.Open(envelope, key, vaultId, "config", "config");
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
