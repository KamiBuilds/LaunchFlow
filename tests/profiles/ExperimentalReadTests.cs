using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using LaunchFlow.Services;

internal static class ExperimentalReadTests
{
    public static void Run()
    {
        var keyPath = Path.Combine(VaultDatabase.Current.DataDirectory, "profile-key.dpapi");
        var wrappedKey = File.ReadAllBytes(keyPath);
        var key = ProtectedData.Unprotect(wrappedKey, null, DataProtectionScope.CurrentUser);
        try
        {
            var expected = RandomNumberGenerator.GetBytes(256 * 1024 + 17);
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = archive.CreateEntry("fixture.bin", CompressionLevel.NoCompression).Open())
                entry.Write(expected);
            var fixture = Encode(zip.ToArray(), key);
            var id = "independent-lfps2";
            var path = Write(id, fixture);
            using (var observer = new ZipObserver(VaultDatabase.Current.DataDirectory))
            {
                // A positive control proves the live observer detects even an immediately deleted file.
                observer.ProveDetection();
                ProfileVaultService.UnsealAll();
                observer.AssertNoZipCreated();
            }
            Check(File.ReadAllBytes(Path.Combine(ProfileVaultService.ProfilesRoot, id, "fixture.bin")).SequenceEqual(expected),
                "independent multi-chunk LFPS2 fixture restores exact data");
            Check(ProtectedData.LastUnprotected!.All(b => b == 0), "LFPS2 successful read zeroes the unwrapped profile key");
            Check(File.ReadAllBytes(path).SequenceEqual(fixture), "LFPS2 read does not migrate or delete its ciphertext");
            Check(File.ReadAllBytes(keyPath).SequenceEqual(wrappedKey), "legacy and LFPS2 reads preserve the original wrapped key");
            var cases = new Dictionary<string, byte[]>();
            var tampered = fixture.ToArray();
            tampered[5 + 4 + 12 + 3] ^= 1;
            cases.Add("tampered-data", tampered);
            var tamperedTag = fixture.ToArray();
            tamperedTag[5 + 4 + 12 + 64 * 1024] ^= 1;
            cases.Add("tampered-tag", tamperedTag);
            cases.Add("truncated-terminator", fixture[..^1]);
            cases.Add("missing-terminator", fixture[..^32]);
            cases.Add("trailing-data", fixture.Concat(new byte[] { 1 }).ToArray());
            var reordered = fixture.ToArray();
            const int recordSize = 64 * 1024 + 32;
            fixture.AsSpan(5 + recordSize, recordSize).CopyTo(reordered.AsSpan(5));
            fixture.AsSpan(5, recordSize).CopyTo(reordered.AsSpan(5 + recordSize));
            cases.Add("reordered-chunks", reordered);
            var invalidLength = fixture.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(invalidLength.AsSpan(5), 64 * 1024 + 1);
            cases.Add("oversized-chunk", invalidLength);
            var negativeLength = fixture.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(negativeLength.AsSpan(5), -1);
            cases.Add("negative-chunk", negativeLength);
            cases.Add("authenticated-invalid-zip", Encode(new byte[] { 1, 2, 3 }, key));
            using (var traversal = new MemoryStream())
            {
                using (var archive = new ZipArchive(traversal, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using (var entry = new StreamWriter(archive.CreateEntry("partial.txt").Open())) entry.Write("synthetic partial");
                    using (var entry = new StreamWriter(archive.CreateEntry("../escape-lfps2.txt").Open())) entry.Write("must not escape");
                }
                cases.Add("authenticated-traversal", Encode(traversal.ToArray(), key));
            }
            foreach (var pair in cases) AssertRejected(pair.Key, pair.Value);
            Check(!File.Exists(Path.Combine(ProfileVaultService.ProfilesRoot, "escape-lfps2.txt")), "LFPS2 extraction blocks traversal");

            var firstBad = Write("aggregate-one", cases["tampered-data"]);
            var secondBad = Write("aggregate-two", cases["trailing-data"]);
            var healthy = Write("aggregate-healthy", fixture);
            try
            {
                AggregateException? failures = null;
                try { ProfileVaultService.UnsealAll(); }
                catch (AggregateException ex) { failures = ex; }
                Check(failures?.InnerExceptions.Count == 2, "LFPS2 reports every failed package in one aggregate");
                Check(File.ReadAllBytes(Path.Combine(ProfileVaultService.ProfilesRoot, "aggregate-healthy", "fixture.bin")).SequenceEqual(expected),
                    "LFPS2 failures do not prevent restoration of independent healthy profiles");
                Check(File.ReadAllBytes(firstBad).SequenceEqual(cases["tampered-data"]) &&
                    File.ReadAllBytes(secondBad).SequenceEqual(cases["trailing-data"]), "aggregate failures retain both ciphertext packages");
            }
            finally
            {
                File.Delete(firstBad);
                File.Delete(secondBad);
                File.Delete(healthy);
            }
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static void RunBudget()
    {
        var previousRoot = VaultDatabase.Current.DataDirectory;
        var root = Path.Combine(Path.GetTempPath(), "launchflow-budget-test-" + Guid.NewGuid().ToString("N"));
        VaultDatabase.Current.DataDirectory = root;
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(ProfileVaultService.SealedRoot);
            File.WriteAllBytes(Path.Combine(root, "profile-key.dpapi"),
                ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
            var path = Path.Combine(ProfileVaultService.SealedRoot, "over-budget.profile.aes");
            // Valid ZIP with one uncompressed synthetic entry. No plaintext fixture file on disk.
            using (var zip = new MemoryStream(257 * 1024 * 1024 + 1024))
            {
                using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
                using (var entry = archive.CreateEntry("large.bin", CompressionLevel.NoCompression).Open())
                {
                    var block = new byte[64 * 1024];
                    for (var i = 0; i < 257 * 16; i++) entry.Write(block);
                }
                zip.Position = 0;
                using var output = File.Create(path);
                Encode(zip, output, key);
            }
            byte[] before;
            using (var input = File.OpenRead(path)) before = SHA256.HashData(input);
            using var observer = new ZipObserver(root);
            AggregateException? failure = null;
            try { ProfileVaultService.UnsealAll(); }
            catch (AggregateException ex) { failure = ex; }
            observer.AssertNoZipCreated();
            Check(ProtectedData.LastUnprotected!.All(b => b == 0), "LFPS2 memory-limit failure zeroes the unwrapped profile key");
            Check(failure?.InnerExceptions.Any(ex => ex is InvalidDataException && ex.Message.Contains("memory limit", StringComparison.Ordinal)) == true,
                "LFPS2 ZIP above 256 MiB fails closed at the explicit memory limit");
            Check(!Directory.Exists(Path.Combine(ProfileVaultService.ProfilesRoot, "over-budget")) &&
                !Directory.EnumerateDirectories(ProfileVaultService.ProfilesRoot, ".restore-*").Any(),
                "LFPS2 memory-limit failure publishes no target and removes staging");
            using var retained = File.OpenRead(path);
            Check(SHA256.HashData(retained).SequenceEqual(before), "LFPS2 memory-limit failure retains exact ciphertext");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            VaultDatabase.Current.DataDirectory = previousRoot;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Independent protocol fixture, not the production writer: [LFPS2], then LE length,
    // nonce[12], ciphertext[length], tag[16]; AAD = LE index[8] + length[4]; authenticated empty terminator.
    private static byte[] Encode(byte[] zip, byte[] key)
    {
        using var input = new MemoryStream(zip, writable: false);
        using var output = new MemoryStream();
        Encode(input, output, key);
        return output.ToArray();
    }

    private static void Encode(Stream zip, Stream output, byte[] key)
    {
        output.Write("LFPS2"u8);
        using var aes = new AesGcm(key, 16);
        var index = 0L;
        var block = new byte[64 * 1024];
        while (true)
        {
            var length = zip.ReadAtLeast(block, block.Length, throwOnEndOfStream: false);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            var aad = new byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(aad, index++);
            BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(8), length);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var encrypted = new byte[length];
            var tag = new byte[16];
            aes.Encrypt(nonce, block.AsSpan(0, length), encrypted, tag, aad);
            output.Write(header);
            output.Write(nonce);
            output.Write(encrypted);
            output.Write(tag);
            if (length == 0) break;
        }
        CryptographicOperations.ZeroMemory(block);
    }

    private static void AssertRejected(string id, byte[] fixture)
    {
        var path = Write(id, fixture);
        try
        {
            using var observer = new ZipObserver(VaultDatabase.Current.DataDirectory);
            AggregateException? failure = null;
            try { ProfileVaultService.UnsealAll(); }
            catch (AggregateException ex) { failure = ex; }
            Check(failure?.InnerExceptions.Count == 1, id + " is rejected and reported");
            Check(ProtectedData.LastUnprotected!.All(b => b == 0), id + " zeroes the unwrapped profile key");
            Check(!Directory.Exists(Path.Combine(ProfileVaultService.ProfilesRoot, id)), id + " publishes no target");
            Check(!Directory.EnumerateDirectories(ProfileVaultService.ProfilesRoot, ".restore-*").Any(), id + " removes partial staging");
            Check(File.ReadAllBytes(path).SequenceEqual(fixture), id + " retains exact ciphertext");
            observer.AssertNoZipCreated();
        }
        finally { File.Delete(path); }
    }

    private static string Write(string id, byte[] bytes)
    {
        var path = Path.Combine(ProfileVaultService.SealedRoot, id + ".profile.aes");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL " + message);
        Console.WriteLine("PASS " + message);
    }

    private sealed class ZipObserver : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly ConcurrentQueue<string> _zipFiles = new();
        private readonly ConcurrentQueue<Exception> _errors = new();
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _barriers = new();
        private readonly string _root;

        public ZipObserver(string root)
        {
            _root = root;
            _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
            _watcher.Created += (_, e) =>
            {
                if (Path.GetFileName(e.FullPath).StartsWith(".zip-", StringComparison.Ordinal)) _zipFiles.Enqueue(e.FullPath);
                if (_barriers.TryGetValue(e.FullPath, out var barrier)) barrier.Set();
            };
            _watcher.Error += (_, e) => _errors.Enqueue(e.GetException());
            _watcher.EnableRaisingEvents = true;
        }

        private void Drain()
        {
            var path = Path.Combine(_root, ".observer-" + Guid.NewGuid().ToString("N"));
            using var barrier = new ManualResetEventSlim();
            _barriers[path] = barrier;
            File.WriteAllText(path, "fixture-only barrier");
            Check(barrier.Wait(TimeSpan.FromSeconds(10)) && _errors.IsEmpty, "filesystem observer drained without lost events");
            _barriers.TryRemove(path, out _);
            File.Delete(path);
        }

        public void ProveDetection()
        {
            var path = Path.Combine(_root, ".zip-observer-control");
            File.WriteAllText(path, "synthetic control, not a profile");
            File.Delete(path);
            Drain();
            Check(_zipFiles.TryDequeue(out var observed) && observed == path, "filesystem observer detects transient plaintext ZIP file creation");
        }

        public void AssertNoZipCreated()
        {
            Drain();
            Check(_zipFiles.IsEmpty, "LFPS2 restore never creates a plaintext .zip- file on disk (live filesystem observer)");
        }

        public void Dispose() => _watcher.Dispose();
    }
}
