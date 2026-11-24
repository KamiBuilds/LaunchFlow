using System.IO;
using System.Security.Cryptography;
using System.Text;
using LaunchFlow.Models;
using Microsoft.Data.Sqlite;

namespace LaunchFlow.Services;

/// <summary>Conservative creation state; none of these failure states authorize deletion.</summary>
public enum VaultCreationOutcome
{
    NotPublished,
    CommitUncertain,
    CandidateCommitted,
    PublicationUncertain,
    Published
}

/// <summary>
/// Creation could not finish safely. Preserve any retained stage and inspect or
/// unlock an existing destination; do not automatically initialize over it.
/// </summary>
public sealed class VaultCreationException : IOException
{
    public VaultCreationOutcome Outcome { get; }
    // For explicit local recovery only. Never include this path in diagnostics.
    public string? RetainedCreationDirectory { get; }

    internal VaultCreationException(VaultCreationOutcome outcome, string? retainedDirectory, Exception inner)
        : base("Vault creation did not finish. Preserve retained creation material; " +
            "inspect or unlock existing storage before retrying.", inner)
    {
        Outcome = outcome;
        RetainedCreationDirectory = retainedDirectory;
    }
}

public sealed partial class VaultDatabase
{
    // Optional hooks are erased from source-linked/platform builds that omit
    // their implementations. Diagnostic adapters must be strictly fail-open.
    partial void TraceCreationStage(string stage);
    partial void CreationOperationForTesting(string operation);

    private const int StrictSchemaVersion = VaultRecordCodec.Version;
    private byte[]? _strictVaultId;
    private byte[]? _strictIdentityToken;
    private byte[]? _strictSnapshotToken;

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

    private static byte[] StrictIdentityToken(StrictMeta meta)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(meta.VaultId);
        hash.AppendData(meta.Salt);
        hash.AppendData(meta.Verifier);
        Span<byte> parameters = stackalloc byte[sizeof(int) * 3];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(parameters, meta.MemoryKb);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(parameters[sizeof(int)..], meta.Iterations);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(parameters[(sizeof(int) * 2)..], meta.Lanes);
        hash.AppendData(parameters);
        return hash.GetHashAndReset();
    }

    private void RequireExpectedStrictIdentity(SqliteConnection connection)
    {
        var current = StrictIdentityToken(ReadStrictMeta(connection));
        byte[]? snapshot = null;
        try
        {
            if (_strictIdentityToken is null ||
                !CryptographicOperations.FixedTimeEquals(_strictIdentityToken, current))
                throw new InvalidOperationException(
                    "The encrypted vault changed on disk. Lock, unlock, and retry.");
            // Key identity alone is not enough: ordinary config/credential writes
            // leave salt/verifier unchanged. Refuse writers whose last-seen sealed
            // snapshot no longer matches disk (independent same-key stale caches).
            snapshot = ComputeStrictSnapshotToken(connection);
            if (_strictSnapshotToken is null ||
                !CryptographicOperations.FixedTimeEquals(_strictSnapshotToken, snapshot))
                throw new InvalidOperationException(
                    "The encrypted vault changed on disk. Lock, unlock, and retry.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(current);
            if (snapshot is not null) CryptographicOperations.ZeroMemory(snapshot);
        }
    }

    private void RefreshStrictIdentity(SqliteConnection connection)
    {
        var next = StrictIdentityToken(ReadStrictMeta(connection));
        if (_strictIdentityToken is not null)
            CryptographicOperations.ZeroMemory(_strictIdentityToken);
        _strictIdentityToken = next;
        var snapshot = ComputeStrictSnapshotToken(connection);
        if (_strictSnapshotToken is not null)
            CryptographicOperations.ZeroMemory(_strictSnapshotToken);
        _strictSnapshotToken = snapshot;
    }

    /// <summary>
    /// Fingerprint of authoritative sealed payload rows (config + credentials).
    /// Independent of key/salt/verifier so same-key writers cannot overwrite a
    /// fresher committed snapshot while still holding an unlocked cache.
    /// </summary>
    private static byte[] ComputeStrictSnapshotToken(SqliteConnection connection)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT sealed FROM secure_objects WHERE id='config';";
            var sealedConfig = cmd.ExecuteScalar() as byte[] ?? Array.Empty<byte>();
            hash.AppendData(BitConverter.GetBytes(sealedConfig.Length));
            hash.AppendData(sealedConfig);
        }
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT workflow_id, sealed FROM secure_credentials ORDER BY workflow_id COLLATE BINARY;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var sealedCred = (byte[])reader[1];
                hash.AppendData(Encoding.UTF8.GetBytes(id));
                hash.AppendData(BitConverter.GetBytes(sealedCred.Length));
                hash.AppendData(sealedCred);
            }
        }
        return hash.GetHashAndReset();
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
                _strictIdentityToken = StrictIdentityToken(meta);
                if (_strictSnapshotToken is not null)
                    CryptographicOperations.ZeroMemory(_strictSnapshotToken);
                _strictSnapshotToken = ComputeStrictSnapshotToken(connection);
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

    /// <summary>
    /// Verifier-only check while unlocked: no ZeroKey, no cache reload, no session bump.
    /// Falls back to <see cref="UnlockStrict"/> when locked.
    /// </summary>
    private bool ConfirmPasswordStrict(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        lock (_gate)
        {
            if (_key is null)
                return UnlockStrict(password);

            byte[]? candidateKey = null;
            try
            {
                if (InspectStorage() != VaultStorageState.PresentNeedsAuthentication)
                    return false;
                using var connection = OpenConnection();
                var meta = ReadStrictMeta(connection);
                if (_strictIdentityToken is not null &&
                    !CryptographicOperations.FixedTimeEquals(
                        _strictIdentityToken, StrictIdentityToken(meta)))
                    return false;
                // Confirm is key/verifier only; snapshot freshness is enforced on writes.
                candidateKey = DeriveKey(password, meta.Salt, meta.MemoryKb, meta.Iterations, meta.Lanes);
                var verifier = VaultRecordCodec.Open(meta.Verifier, candidateKey, meta.VaultId,
                    "verifier", "verifier");
                var ok = Encoding.UTF8.GetString(verifier) == "LaunchFlow.Vault.v3";
                CryptographicOperations.ZeroMemory(verifier);
                return ok;
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
        TraceCreationStage("VaultCreateAdmission");
        CreationOperationForTesting("VaultCreateAdmission");
        ArgumentNullException.ThrowIfNull(config);
        if (secrets is { Count: > 0 }) EnterprisePolicy.Current.RequireCredentialStorage();
        if (!PasswordStrength.MeetsMinimum(password))
            throw new ArgumentException("Choose a stronger master password.");

        lock (_gate)
        {
            if (_key is not null) throw new InvalidOperationException("Vault is already unlocked.");
            RequireNewCreationDestination();
            TraceCreationStage("VaultCreateCloneConfig");
            CreationOperationForTesting("VaultCreateCloneConfig");
            var candidateConfig = CloneConfig(config);
            var candidateSecrets = CloneSecrets(secrets ?? new Dictionary<Guid, SiteCredential>());
            byte[]? serial = null, key = null, identity = null, snapshot = null;
            string? stage = null;
            SqliteConnection? connection = null;
            SqliteTransaction? transaction = null;
            FileStream? reservation = null;
            var commitAttempted = false;
            var committed = false;
            var publishAttempted = false;
            var published = false;
            try
            {
                TraceCreationStage("VaultCreateSerializeConfig");
                CreationOperationForTesting("VaultCreateSerializeConfig");
                serial = Serialize(candidateConfig);
                SensitiveBufferForTesting("CreationConfig", serial);
                TraceCreationStage("VaultCreateRandomMaterial");
                CreationOperationForTesting("VaultCreateRandomMaterial");
                var salt = RandomNumberGenerator.GetBytes(SaltSize);
                var vaultId = RandomNumberGenerator.GetBytes(16);
                TraceCreationStage("VaultCreateDeriveKey");
                CreationOperationForTesting("VaultCreateDeriveKey");
                key = DeriveKey(password, salt, ArgonMemoryKb, ArgonIterations, ArgonLanes);
                SensitiveBufferForTesting("CreationKey", key);
                TraceCreationStage("VaultCreateOpenConnection");
                CreationOperationForTesting("VaultCreateOpenConnection");
                Directory.CreateDirectory(DataDirectory);
                var nextStage = Path.Combine(DataDirectory, CreationStagePrefix + Guid.NewGuid().ToString("N"));
                // Exclusive reservation arbitrates even a stage-name collision.
                // No existing stage is adopted, and admission never removes one.
                reservation = new FileStream(nextStage + ".lease", FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                if (Directory.Exists(nextStage) || File.Exists(nextStage))
                    throw new IOException("Creation staging location already exists.");
                Directory.CreateDirectory(nextStage);
                stage = nextStage;
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var candidate = Path.Combine(stage, "candidate.db");
                using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = candidate, Mode = SqliteOpenMode.ReadWrite, Pooling = false
                }.ToString());
                connection.Open();
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA secure_delete=ON;";
                    pragma.ExecuteNonQuery();
                }
                TraceCreationStage("VaultCreateBeginTransaction");
                CreationOperationForTesting("VaultCreateBeginTransaction");
                transaction = connection.BeginTransaction();
                TraceCreationStage("VaultCreateEnsureSchema");
                CreationOperationForTesting("VaultCreateEnsureSchema");
                EnsureStrictSchema(connection);
                TraceCreationStage("VaultCreateSealVerifier");
                CreationOperationForTesting("VaultCreateSealVerifier");
                var verifier = VaultRecordCodec.Seal(Encoding.UTF8.GetBytes("LaunchFlow.Vault.v3"),
                    key, vaultId, "verifier", "verifier");
                TraceCreationStage("VaultCreateInsertMeta");
                CreationOperationForTesting("VaultCreateInsertMeta");
                InsertStrictMeta(connection, vaultId, salt, verifier);
                TraceCreationStage("VaultCreateWriteConfig");
                CreationOperationForTesting("VaultCreateWriteConfig");
                WriteSealed(connection, "config", serial, key, vaultId);
                TraceCreationStage("VaultCreatePersistCredentials");
                CreationOperationForTesting("VaultCreatePersistCredentials");
                PersistCredentials(connection, candidateSecrets, key, vaultId);
                // These fallible tokens describe the same transaction snapshot;
                // prepare them before commit, not after publishing a live key.
                TraceCreationStage("VaultCreateIdentityToken");
                CreationOperationForTesting("VaultCreateIdentityToken");
                identity = StrictIdentityToken(
                    new StrictMeta(vaultId, salt, verifier, ArgonMemoryKb, ArgonIterations, ArgonLanes));
                SensitiveBufferForTesting("CreationIdentity", identity);
                TraceCreationStage("VaultCreateSnapshotToken");
                CreationOperationForTesting("VaultCreateSnapshotToken");
                snapshot = ComputeStrictSnapshotToken(connection);
                SensitiveBufferForTesting("CreationSnapshot", snapshot);
                TraceCreationStage("VaultCreateCommit");
                CreationOperationForTesting("VaultCreateCommit");
                commitAttempted = true;
                transaction.Commit();
                CreationOperationForTesting("CommitReturnedBeforeOutcomeRecorded");
                committed = true;
                TraceCreationStage("VaultCreateDisposeResources");
                CreationOperationForTesting("VaultCreateDisposeResources");
                transaction.Dispose();
                transaction = null;
                using (var checkpoint = connection.CreateCommand())
                {
                    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    using var result = checkpoint.ExecuteReader();
                    if (!result.Read() || result.GetInt32(0) != 0 || result.GetInt32(1) != 0)
                        throw new IOException("Creation checkpoint did not complete.");
                }
                connection.Dispose();
                connection = null;
                // Creation alone is nonpooled. Final close must remove all WAL
                // companions before the main file can be safely published.
                if (File.Exists(candidate + "-wal") || File.Exists(candidate + "-shm") ||
                    File.Exists(candidate + "-journal"))
                    throw new IOException("Creation companions are still present.");
                TraceCreationStage("VaultCreatePublish");
                CreationOperationForTesting("VaultCreatePublish");
                RequireCreationTargetAbsent();
                CreationOperationForTesting("PublishTargetCheckedBeforeMove");
                publishAttempted = true;
                PublishCreationCandidate(candidate);
                CreationOperationForTesting("PublishReturnedBeforeOutcomeRecorded");
                published = true;
                TraceCreationStage("VaultCreateCleanup");
                CreationOperationForTesting("VaultCreateCleanup");
                Directory.Delete(stage);
                stage = null;
                reservation.Dispose();
                reservation = null;
                TraceCreationStage("VaultCreatePublishCaches");
                CreationOperationForTesting("VaultCreatePublishCaches");
                _key = key;
                _strictVaultId = vaultId;
                _strictIdentityToken = identity;
                _strictSnapshotToken = snapshot;
                _sessionGeneration++;
                key = identity = snapshot = null;
                _configCache = candidateConfig;
                _secretsCache = candidateSecrets;
            }
            catch (Exception primary)
            {
                var errors = new List<Exception> { primary };
                // Unwind never emits a generic stage over the failed operation.
                // Preserve primary and secondary disposal/cleanup exceptions.
                try { transaction?.Dispose(); } catch (Exception secondary) { errors.Add(secondary); }
                try { connection?.Dispose(); } catch (Exception secondary) { errors.Add(secondary); }
                if (stage is not null && !commitAttempted && errors.Count == 1)
                {
                    try
                    {
                        CreationOperationForTesting("VaultCreateCleanup");
                        foreach (var suffix in new[] { "-wal", "-shm", "-journal", "" })
                            File.Delete(Path.Combine(stage, "candidate.db") + suffix);
                        Directory.Delete(stage);
                        stage = null;
                    }
                    catch (Exception secondary) { errors.Add(secondary); }
                }
                try { reservation?.Dispose(); } catch (Exception secondary) { errors.Add(secondary); }
                if (commitAttempted || stage is not null || errors.Count != 1)
                {
                    var outcome = published ? VaultCreationOutcome.Published :
                        publishAttempted ? VaultCreationOutcome.PublicationUncertain :
                        committed ? VaultCreationOutcome.CandidateCommitted :
                        commitAttempted ? VaultCreationOutcome.CommitUncertain : VaultCreationOutcome.NotPublished;
                    throw new VaultCreationException(outcome, stage,
                        errors.Count == 1 ? primary : new AggregateException("Vault creation and cleanup failed.", errors));
                }
                throw;
            }
            finally
            {
                if (serial is not null) CryptographicOperations.ZeroMemory(serial);
                if (key is not null) CryptographicOperations.ZeroMemory(key);
                if (identity is not null) CryptographicOperations.ZeroMemory(identity);
                if (snapshot is not null) CryptographicOperations.ZeroMemory(snapshot);
            }
            TraceCreationStage("VaultCreateComplete");
        }
    }

    // Windows MoveFile refuses an existing destination. On Unix, the managed
    // no-overwrite move can still race an independent creator; link provides
    // atomic no-clobber publication of the already-closed same-volume file.
    // An unlink failure is deliberately uncertain and preserves both links.
    private void PublishCreationCandidate(string candidate)
    {
        if (OperatingSystem.IsWindows()) File.Move(candidate, DatabasePath, overwrite: false);
        else
        {
            if (LinkCreationCandidate(candidate, DatabasePath) != 0)
                throw new IOException("Creation publication was refused.",
                    new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError()));
            File.Delete(candidate);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkCreationCandidate(string source, string destination);

    private string CreationStagePrefix => "." + Path.GetFileName(DatabasePath) + ".create-";

    private static bool CreationPathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private bool HasCreationArtifacts()
    {
        if (!CreationPathExists(DataDirectory)) return false;
        foreach (var path in Directory.EnumerateFileSystemEntries(DataDirectory, CreationStagePrefix + "*"))
        {
            var attributes = File.GetAttributes(path);
            // Reservations contain no vault data. Empty directories likewise
            // cannot hide a committed candidate. Preserve both, but do not let
            // an interrupted attempt with no material disable ordinary setup.
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                if ((attributes & FileAttributes.Directory) != 0 &&
                    !Directory.EnumerateFileSystemEntries(path).Any()) continue;
                if ((attributes & FileAttributes.Directory) == 0 &&
                    path.EndsWith(".lease", StringComparison.Ordinal) && new FileInfo(path).Length == 0) continue;
            }
            return true;
        }
        return false;
    }

    private void RequireCreationTargetAbsent()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (CreationPathExists(DatabasePath + suffix))
                throw new InvalidOperationException("Vault database or companion already exists.");
    }

    private void RequireNewCreationDestination()
    {
        RequireCreationTargetAbsent();
        if (HasCreationArtifacts())
            throw new InvalidOperationException(
                "A previous creation stage needs recovery. Preserve it before creating a new vault.");
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
            using var transaction = connection.BeginTransaction();
            RequireExpectedStrictIdentity(connection);
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
                _strictIdentityToken = StrictIdentityToken(
                    new StrictMeta(oldMeta.VaultId, nextSalt, verifier,
                        ArgonMemoryKb, ArgonIterations, ArgonLanes));
                if (_strictSnapshotToken is not null)
                    CryptographicOperations.ZeroMemory(_strictSnapshotToken);
                _strictSnapshotToken = ComputeStrictSnapshotToken(connection);
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
