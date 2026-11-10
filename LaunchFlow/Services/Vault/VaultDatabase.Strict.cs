using System.IO;
using System.Security.Cryptography;
using System.Text;
using LaunchFlow.Models;
using Microsoft.Data.Sqlite;

namespace LaunchFlow.Services;

public sealed partial class VaultDatabase
{
    private const int StrictSchemaVersion = VaultRecordCodec.Version;
    private byte[]? _strictVaultId;

    private sealed record StrictMeta(
        byte[] VaultId, byte[] Salt, byte[] Verifier, int MemoryKb, int Iterations, int Lanes);

    private static void EnsureStrictSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS secure_meta (
              id INTEGER PRIMARY KEY CHECK (id=1),
              vault_id BLOB NOT NULL,
              salt BLOB NOT NULL,
              verifier BLOB NOT NULL,
              argon_memory_kb INTEGER NOT NULL,
              argon_iterations INTEGER NOT NULL,
              argon_lanes INTEGER NOT NULL,
              schema_version INTEGER NOT NULL CHECK (schema_version=3)
            );
            CREATE TABLE IF NOT EXISTS secure_objects (
              id TEXT PRIMARY KEY NOT NULL,
              sealed BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS secure_credentials (
              workflow_id TEXT PRIMARY KEY NOT NULL,
              sealed BLOB NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private VaultStorageState InspectStrictStructure(SqliteConnection connection)
    {
        try
        {
            if (!HasColumns(connection, "secure_meta", "id", "vault_id", "salt", "verifier",
                    "argon_memory_kb", "argon_iterations", "argon_lanes", "schema_version") ||
                !HasColumns(connection, "secure_objects", "id", "sealed") ||
                !HasColumns(connection, "secure_credentials", "workflow_id", "sealed"))
                return VaultStorageState.IncompleteOrUnsupported;
            var meta = ReadStrictMeta(connection);
            if (meta.VaultId.Length != 16 || meta.Salt.Length != SaltSize ||
                meta.Verifier.Length < 4 + NonceSize + TagSize ||
                meta.MemoryKb < 16 * 1024 || meta.MemoryKb > 512 * 1024 ||
                meta.Iterations < 1 || meta.Iterations > 12 ||
                meta.Lanes < 1 || meta.Lanes > 8)
                return VaultStorageState.IncompleteOrUnsupported;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT sealed FROM secure_objects WHERE id='config';";
            var config = cmd.ExecuteScalar() as byte[];
            return config is { Length: >= 32 } &&
                Encoding.ASCII.GetString(config, 0, 4) == "LFV3"
                ? VaultStorageState.PresentNeedsAuthentication
                : VaultStorageState.IncompleteOrUnsupported;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or
            InvalidOperationException or InvalidCastException or FormatException or OverflowException)
        {
            return VaultStorageState.IncompleteOrUnsupported;
        }
    }

    private static StrictMeta ReadStrictMeta(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT vault_id,salt,verifier,argon_memory_kb,argon_iterations,argon_lanes," +
            "schema_version FROM secure_meta WHERE id=1;";
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.GetInt32(6) != StrictSchemaVersion)
            throw new InvalidDataException("Strict vault metadata is missing or unsupported.");
        return new StrictMeta((byte[])r[0], (byte[])r[1], (byte[])r[2],
            r.GetInt32(3), r.GetInt32(4), r.GetInt32(5));
    }

    private static void InsertStrictMeta(SqliteConnection connection,
        byte[] vaultId, byte[] salt, byte[] verifier)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO secure_meta " +
            "(id,vault_id,salt,verifier,argon_memory_kb,argon_iterations,argon_lanes,schema_version) " +
            "VALUES(1,$id,$salt,$v,$m,$i,$l,3);";
        cmd.Parameters.AddWithValue("$id", vaultId);
        cmd.Parameters.AddWithValue("$salt", salt);
        cmd.Parameters.AddWithValue("$v", verifier);
        cmd.Parameters.AddWithValue("$m", ArgonMemoryKb);
        cmd.Parameters.AddWithValue("$i", ArgonIterations);
        cmd.Parameters.AddWithValue("$l", ArgonLanes);
        cmd.ExecuteNonQuery();
    }

    private bool UnlockStrict(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        lock (_gate)
        {
            byte[]? candidateKey = null;
            try
            {
                if (InspectStorage() != VaultStorageState.PresentNeedsAuthentication)
                    return false;
                using var connection = OpenConnection();
                var meta = ReadStrictMeta(connection);
                candidateKey = DeriveKey(password, meta.Salt, meta.MemoryKb, meta.Iterations, meta.Lanes);
                var verifier = VaultRecordCodec.Open(meta.Verifier, candidateKey, meta.VaultId,
                    "verifier", "verifier");
                if (Encoding.UTF8.GetString(verifier) != "LaunchFlow.Vault.v3")
                    return false;
                CryptographicOperations.ZeroMemory(verifier);

                var bytes = ReadSealed(connection, "config", candidateKey, meta.VaultId);
                var config = Deserialize<Configuration>(bytes)
                    ?? throw new InvalidDataException("Vault configuration was empty.");
                CryptographicOperations.ZeroMemory(bytes);
                // Verify every authoritative credential before publishing any key
                // or cache, not only the credential later selected in the editor.
                var secrets = ReadCredentialRows(connection, candidateKey, meta.VaultId);

                ZeroKey();
                _key = candidateKey;
                _strictVaultId = meta.VaultId.ToArray();
                _sessionGeneration++;
                candidateKey = null;
                _configCache = CloneConfig(config);
                _secretsCache = CloneSecrets(secrets);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (candidateKey is not null)
                    CryptographicOperations.ZeroMemory(candidateKey);
            }
        }
    }

    private void InitializeStrict(string password, Configuration config,
        Dictionary<Guid, SiteCredential>? secrets)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (secrets is { Count: > 0 }) EnterprisePolicy.Current.RequireCredentialStorage();
        if (!PasswordStrength.MeetsMinimum(password))
            throw new ArgumentException("Choose a stronger master password.");

        lock (_gate)
        {
            if (_key is not null) throw new InvalidOperationException("Vault is already unlocked.");
            if (File.Exists(DatabasePath)) throw new InvalidOperationException("Vault database already exists.");
            var candidateConfig = CloneConfig(config);
            var candidateSecrets = CloneSecrets(secrets ?? new Dictionary<Guid, SiteCredential>());
            var serial = Serialize(candidateConfig);
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var vaultId = RandomNumberGenerator.GetBytes(16);
            var key = DeriveKey(password, salt, ArgonMemoryKb, ArgonIterations, ArgonLanes);
            try
            {
                using var connection = OpenConnection(create: true);
                using var transaction = connection.BeginTransaction();
                EnsureStrictSchema(connection);
                var verifier = VaultRecordCodec.Seal(Encoding.UTF8.GetBytes("LaunchFlow.Vault.v3"),
                    key, vaultId, "verifier", "verifier");
                InsertStrictMeta(connection, vaultId, salt, verifier);
                WriteSealed(connection, "config", serial, key, vaultId);
                PersistCredentials(connection, candidateSecrets, key, vaultId);
                transaction.Commit();

                _key = key;
                _strictVaultId = vaultId;
                _sessionGeneration++;
                key = null!;
                _configCache = candidateConfig;
                _secretsCache = candidateSecrets;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serial);
                if (key is not null) CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private void ChangeMasterStrict(string currentPassword, string newPassword)
    {
        if (!PasswordStrength.MeetsMinimum(newPassword))
            throw new ArgumentException("Choose a stronger new master password.");
        lock (_gate)
        {
            if (_key is null && !UnlockStrict(currentPassword))
                throw new InvalidOperationException("Current master password is incorrect.");
            using var connection = OpenConnection();
            var oldMeta = ReadStrictMeta(connection);
            var check = DeriveKey(currentPassword, oldMeta.Salt, oldMeta.MemoryKb,
                oldMeta.Iterations, oldMeta.Lanes);
            try
            {
                var verifier = VaultRecordCodec.Open(oldMeta.Verifier, check, oldMeta.VaultId,
                    "verifier", "verifier");
                if (Encoding.UTF8.GetString(verifier) != "LaunchFlow.Vault.v3")
                    throw new InvalidOperationException("Current master password is incorrect.");
                CryptographicOperations.ZeroMemory(verifier);
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Current master password is incorrect.");
            }
            finally { CryptographicOperations.ZeroMemory(check); }

            var config = LoadConfigurationUnlocked();
            var credentials = CloneSecrets(LoadSecretsUnlocked());
            var nextSalt = RandomNumberGenerator.GetBytes(SaltSize);
            var key = DeriveKey(newPassword, nextSalt, ArgonMemoryKb, ArgonIterations, ArgonLanes);
            try
            {
                using var transaction = connection.BeginTransaction();
                var verifier = VaultRecordCodec.Seal(Encoding.UTF8.GetBytes("LaunchFlow.Vault.v3"),
                    key, oldMeta.VaultId, "verifier", "verifier");
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "UPDATE secure_meta SET salt=$s, verifier=$v," +
                        "argon_memory_kb=$m, argon_iterations=$i, argon_lanes=$l WHERE id=1;";
                    cmd.Parameters.AddWithValue("$s", nextSalt);
                    cmd.Parameters.AddWithValue("$v", verifier);
                    cmd.Parameters.AddWithValue("$m", ArgonMemoryKb);
                    cmd.Parameters.AddWithValue("$i", ArgonIterations);
                    cmd.Parameters.AddWithValue("$l", ArgonLanes);
                    if (cmd.ExecuteNonQuery() != 1)
                        throw new InvalidDataException("Missing strict metadata.");
                }
                WriteConfigurationSealed(connection, config, key, oldMeta.VaultId);
                PersistCredentials(connection, credentials, key, oldMeta.VaultId);
                try { transaction.Commit(); }
                catch
                {
                    ZeroKey();
                    _configCache = null;
                    _secretsCache = null;
                    throw;
                }
                ZeroKey();
                _key = key;
                _strictVaultId = oldMeta.VaultId.ToArray();
                _sessionGeneration++;
                key = null!;
                _configCache = CloneConfig(config);
                _secretsCache = CloneSecrets(credentials);
            }
            finally
            {
                if (key is not null) CryptographicOperations.ZeroMemory(key);
            }
        }
    }
}
