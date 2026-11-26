using System.IO;
using System.Security.Cryptography;

namespace LaunchFlow.Services;

public sealed partial class VaultDatabase
{
    // SQLite read-only connections can create/change WAL and SHM. Only the owned
    // copy is opened by SQLite; ordinary file reads never authorize the vault.
    private sealed class InspectionSnapshot : IDisposable
    {
        private static readonly string[] Suffixes = { "", "-wal", "-shm", "-journal" };
        private readonly string _source;
        private readonly string _directory;
        private readonly List<InspectionFile> _files = new();
        public string DatabasePath { get; }

        public InspectionSnapshot(string source)
        {
            _source = Path.GetFullPath(source);
            RequireOrdinaryInspectionParents(_source);
            _directory = Directory.CreateTempSubdirectory("LaunchFlow-VaultInspection-").FullName;
            DatabasePath = Path.Combine(_directory, "candidate.db");
            try
            {
                foreach (var suffix in Suffixes)
                {
                    var path = _source + suffix;
                    if (!CreationPathExists(path))
                    {
                        if (suffix == "") throw new IOException("Vault inspection source is unavailable.");
                        continue;
                    }
                    RequireOrdinaryInspectionFile(path);
                    // Copy journals too: committed PERSIST/empty journals can be
                    // inert. SQLite's unchanged read-only classifier decides whether
                    // the owned set requires recovery; never recover the live set.
                    var stamp = File.GetLastWriteTimeUtc(path);
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var file = new InspectionFile(suffix, stream, stream.Length, stamp);
                    _files.Add(file);
                }
                foreach (var file in _files)
                {
                    using (var output = new FileStream(DatabasePath + file.Suffix,
                               FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        file.Stream.CopyTo(output);
                    using var copied = File.OpenRead(DatabasePath + file.Suffix);
                    file.Digest = SHA256.HashData(copied);
                }
                VerifyUnchanged();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void VerifyUnchanged()
        {
            // This detects observable changes, not an atomic snapshot against a
            // hostile writer. Refuse rather than accept a known inconsistent set.
            RequireOrdinaryInspectionParents(_source);
            foreach (var suffix in Suffixes)
            {
                var file = _files.SingleOrDefault(f => f.Suffix == suffix);
                var path = _source + suffix;
                if (CreationPathExists(path) != (file is not null))
                    throw new IOException("Vault inspection source set changed.");
                if (file is null) continue;
                RequireOrdinaryInspectionFile(path);
                if (file.Stream.Length != file.Length || File.GetLastWriteTimeUtc(path) != file.Stamp)
                    throw new IOException("Vault inspection source changed.");
                using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (current.Length != file.Length ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), file.Digest))
                    throw new IOException("Vault inspection source changed.");
                if (File.GetLastWriteTimeUtc(path) != file.Stamp)
                    throw new IOException("Vault inspection source changed.");
            }
        }

        public void Dispose()
        {
            try { foreach (var file in _files) file.Stream.Dispose(); }
            finally { Directory.Delete(_directory, recursive: true); }
        }

        private sealed class InspectionFile(string suffix, FileStream stream, long length, DateTime stamp)
        {
            public string Suffix { get; } = suffix;
            public FileStream Stream { get; } = stream;
            public long Length { get; } = length;
            public DateTime Stamp { get; } = stamp;
            public byte[] Digest { get; set; } = Array.Empty<byte>();
        }
    }

    private static void RequireOrdinaryInspectionFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Vault inspection requires ordinary files.");
    }

    private static void RequireOrdinaryInspectionParents(string path)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null;
             parent = Path.GetDirectoryName(parent))
        {
            if (!CreationPathExists(parent)) continue;
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0 || !Directory.Exists(parent))
                throw new IOException("Vault inspection requires ordinary directories.");
        }
    }
}
