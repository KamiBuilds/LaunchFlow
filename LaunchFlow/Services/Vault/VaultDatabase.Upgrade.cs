using System.IO;
using System.Security.Cryptography;
using System.Text;
using LaunchFlow.Models;
using Microsoft.Data.Sqlite;

namespace LaunchFlow.Services;

public sealed partial class VaultDatabase
{
    private int _upgradeCancelEpoch;

    private sealed record LegacySnapshot(
        Configuration Configuration, Dictionary<Guid, SiteCredential> Credentials);

    /// <summary>
    /// The only v1/v2 decryption entrypoint. It does not publish a vault session.
    /// All live credentials and configuration remain inaccessible until an
    /// explicit verified-backup conversion commits and the v3 session is adopted.
    /// </summary>
    private LegacySnapshot ReadLegacySnapshot(string path, string password)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        if (!HasColumns(connection, "vault_meta", "id", "salt", "verifier",
                "argon_memory_kb", "argon_iterations", "argon_lanes") ||
            !HasColumns(connection, "objects", "id", "sealed"))
            throw new InvalidDataException("Legacy vault metadata is incomplete.");
        byte[] salt, verifier;
        int memory, passes, lanes;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT salt,verifier,argon_memory_kb,argon_iterations,argon_lanes " +
                "FROM vault_meta WHERE id=1;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("Legacy metadata row is missing.");
            salt = (byte[])reader[0];
            verifier = (byte[])reader[1];
            memory = reader.GetInt32(2);
            passes = reader.GetInt32(3);
            lanes = reader.GetInt32(4);
        }
        if (salt.Length != SaltSize || memory < 16 * 1024 || memory > 512 * 1024 ||
            passes < 1 || passes > 12 || lanes < 1 || lanes > 8)
            throw new InvalidDataException("Invalid legacy key-derivation metadata.");
        var key = DeriveKey(password, salt, memory, passes, lanes);
        try
        {
            if (!TryDecrypt(verifier, key, out var verified) ||
                Encoding.UTF8.GetString(verified) != "LaunchFlow.Vault.v1")
                throw new UnauthorizedAccessException("Legacy master password was not accepted.");
            CryptographicOperations.ZeroMemory(verified);

            byte[] LoadLegacyObject(string id)
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT sealed FROM objects WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", id);
                var bytes = cmd.ExecuteScalar() as byte[]
                    ?? throw new InvalidDataException("Legacy configuration is missing.");
                if (!TryDecrypt(bytes, key, out var plain))
                    throw new CryptographicException("Legacy configuration failed authentication.");
                return plain;
            }

            var configBytes = LoadLegacyObject("config");
            Configuration config;
            try
            {
                config = Deserialize<Configuration>(configBytes)
                    ?? throw new InvalidDataException("Legacy configuration is empty.");
            }
            finally { CryptographicOperations.ZeroMemory(configBytes); }

            if (config.Workflows.Select(x => x.Id).Distinct().Count() != config.Workflows.Count ||
                config.Workflows.Any(w => w.Id == Guid.Empty))
                throw new InvalidDataException("Legacy workflows have invalid identities.");

            var credentials = new Dictionary<Guid, SiteCredential>();
            if (HasColumns(connection, "credentials", "workflow_id", "sealed"))
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT workflow_id,sealed FROM credentials;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var identity = reader.GetString(0);
                    if (!Guid.TryParse(identity, out var id) || id == Guid.Empty ||
                        credentials.ContainsKey(id))
                        throw new InvalidDataException("Invalid or duplicate legacy credential identity.");
                    if (!TryDecrypt((byte[])reader[1], key, out var plain))
                        throw new CryptographicException("Legacy credential authentication failed.");
                    try
                    {
                        credentials.Add(id, Deserialize<SiteCredential>(plain)
                            ?? throw new InvalidDataException("Legacy credential was empty."));
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
            }

            using var blobQuery = connection.CreateCommand();
            blobQuery.CommandText = "SELECT sealed FROM objects WHERE id='credentials';";
            var oldBlob = blobQuery.ExecuteScalar() as byte[];
            if (oldBlob is not null)
            {
                if (credentials.Count > 0)
                    throw new InvalidDataException("Ambiguous legacy credential representations.");
                if (!TryDecrypt(oldBlob, key, out var plain))
                    throw new CryptographicException("Legacy credential blob authentication failed.");
                try
                {
                    credentials = Deserialize<Dictionary<Guid, SiteCredential>>(plain)
                        ?? throw new InvalidDataException("Legacy credential blob is empty.");
                    if (credentials.ContainsKey(Guid.Empty))
                        throw new InvalidDataException("Legacy credential has an empty workflow ID.");
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }

            return new LegacySnapshot(config, CloneSecrets(credentials));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static bool SameLegacySnapshot(LegacySnapshot a, LegacySnapshot b)
    {
        if (!Serialize(a.Configuration).AsSpan().SequenceEqual(Serialize(b.Configuration)) ||
            a.Credentials.Count != b.Credentials.Count)
            return false;
        return a.Credentials.All(pair =>
            b.Credentials.TryGetValue(pair.Key, out var other) && pair.Value == other);
    }

    private static void WriteStrictUpgrade(SqliteConnection connection, LegacySnapshot source,
        byte[] passwordKey, byte[] salt, byte[] vaultId)
    {
        EnsureStrictSchema(connection);
        var verifier = VaultRecordCodec.Seal(Encoding.UTF8.GetBytes("LaunchFlow.Vault.v3"),
            passwordKey, vaultId, "verifier", "verifier");
        InsertStrictMeta(connection, vaultId, salt, verifier);
        var candidateConfig = CloneConfig(source.Configuration);
        foreach (var workflow in candidateConfig.Workflows)
            workflow.Enabled = false;
        var serializedConfig = Serialize(candidateConfig);
        byte[] config;
        try
        {
            config = VaultRecordCodec.Seal(serializedConfig, passwordKey, vaultId,
                "config", "config");
        }
        finally { CryptographicOperations.ZeroMemory(serializedConfig); }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO secure_objects(id,sealed) VALUES('config',$s);";
            cmd.Parameters.AddWithValue("$s", config);
            cmd.ExecuteNonQuery();
        }
        foreach (var pair in source.Credentials)
        {
            var owner = pair.Key.ToString("N");
            var serializedCredential = Serialize(pair.Value);
            byte[] value;
            try
            {
                value = VaultRecordCodec.Seal(serializedCredential, passwordKey, vaultId,
                    "credential", owner);
            }
            finally { CryptographicOperations.ZeroMemory(serializedCredential); }
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO secure_credentials(workflow_id,sealed) VALUES($id,$s);";
            cmd.Parameters.AddWithValue("$id", owner);
            cmd.Parameters.AddWithValue("$s", value);
            cmd.ExecuteNonQuery();
        }

        // Old processes can recreate these inert old tables, but none of their
        // SQL statements names an authoritative v3 table.
        using var remove = connection.CreateCommand();
        remove.CommandText =
            "DROP TABLE IF EXISTS credentials; DROP TABLE IF EXISTS objects; " +
            "DROP TABLE IF EXISTS vault_meta;";
        remove.ExecuteNonQuery();
    }

    /// <summary>
    /// One-way conversion only after caller's explicit consent. Retains an
    /// independently verified, WAL-consistent encrypted recovery snapshot.
    /// Never overwrites a preexisting recovery copy or a failed upgrade.
    /// </summary>
    public string UpgradeLegacyVault(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Legacy master password is required.");
        lock (_gate)
        {
            if (_key is not null)
                throw new InvalidOperationException("Lock the vault before a format upgrade.");
            if (EnterprisePolicy.Current.DisableCredentialStorage)
                throw new InvalidOperationException(
                    "Credential storage policy blocks a lossless security upgrade.");
            if (InspectStorage() != VaultStorageState.LegacyUpgradeRequired)
                throw new InvalidOperationException("This vault is not an eligible legacy format.");

            var cancelEpoch = System.Threading.Volatile.Read(ref _upgradeCancelEpoch);
            // Password and every encrypted legacy record are authenticated before
            // any backup or write operation begins.
            var original = ReadLegacySnapshot(DatabasePath, password);
            var backupRoot = Path.Combine(DataDirectory, "recovery");
            Directory.CreateDirectory(backupRoot);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(backupRoot,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var backup = Path.Combine(backupRoot, "launchflow-legacy-" +
                DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" +
                Guid.NewGuid().ToString("N") + ".db");
            // Reserve a collision-free destination without replacing any file.
            using (new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = backup, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            // Reopen from the copied SQLite data using only the legacy decoder.
            var recovered = ReadLegacySnapshot(backup, password);
            if (!SameLegacySnapshot(original, recovered))
                throw new InvalidDataException("Recovery copy failed independent verification.");
            if (System.Threading.Volatile.Read(ref _upgradeCancelEpoch) != cancelEpoch)
                throw new OperationCanceledException("Vault was locked during conversion.");

            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var vaultId = RandomNumberGenerator.GetBytes(16);
            var candidateKey = DeriveKey(password, salt, ArgonMemoryKb, ArgonIterations, ArgonLanes);
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                // Re-read while holding the SQLite write transaction. An older
                // writer's change since backup must abort before conversion.
                var current = ReadLegacySnapshot(DatabasePath, password);
                if (!SameLegacySnapshot(original, current))
                    throw new InvalidDataException("Legacy database changed during upgrade.");
                WriteStrictUpgrade(connection, current, candidateKey, salt, vaultId);
                if (System.Threading.Volatile.Read(ref _upgradeCancelEpoch) != cancelEpoch)
                    throw new OperationCanceledException("Vault authorization changed.");
                transaction.Commit();

                // Commit is already durable; a late lock revokes access but
                // cannot turn an upgraded database back into a legacy file.
                if (System.Threading.Volatile.Read(ref _upgradeCancelEpoch) != cancelEpoch)
                    return backup;

                // An independent cold reader must verify the committed strict
                // verifier, configuration and every credential before a normal
                // runtime key/session is published.
                var cold = new VaultDatabase(DatabasePath);
                try
                {
                    if (!cold.UnlockStrict(password))
                        throw new InvalidDataException("Committed strict vault could not be reopened.");
                    var accepted = new LegacySnapshot(cold.LoadConfiguration(),
                        CloneSecrets(cold.LoadSecretsUnlocked()));
                    var expected = CloneConfig(current.Configuration);
                    foreach (var workflow in expected.Workflows) workflow.Enabled = false;
                    if (!SameLegacySnapshot(new LegacySnapshot(expected, current.Credentials), accepted))
                        throw new InvalidDataException("Strict cold recovery did not retain the accepted data.");
                }
                finally { cold.Lock(); }

                if (System.Threading.Volatile.Read(ref _upgradeCancelEpoch) != cancelEpoch)
                    return backup;
                _key = candidateKey;
                _strictVaultId = vaultId;
                _configCache = CloneConfig(current.Configuration);
                foreach (var workflow in _configCache.Workflows) workflow.Enabled = false;
                _secretsCache = CloneSecrets(current.Credentials);
                _sessionGeneration++;
                candidateKey = null!;
                return backup;
            }
            finally
            {
                if (candidateKey is not null)
                    CryptographicOperations.ZeroMemory(candidateKey);
            }
        }
    }
}
