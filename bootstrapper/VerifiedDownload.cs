using System.Security.Cryptography;

namespace LaunchFlow.UniversalInstaller;

internal static class VerifiedDownload
{
    // Output remains untrusted until this method returns; the caller owns cleanup and launch.
    internal static async Task CopyAsync(Stream input, Stream output, ReleaseInfo release, long? contentLength,
        Action<int>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (release.Size <= 0 || release.Size > ReleasePolicy.MaxDownloadBytes ||
            (contentLength.HasValue && contentLength.Value != release.Size))
            throw new InvalidDataException("The download size does not match the release. Installation is blocked.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int bytes;
        while ((bytes = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (bytes > release.Size - copied)
                throw new InvalidDataException("The download exceeds the declared release size.");
            hash.AppendData(buffer, 0, bytes);
            await output.WriteAsync(buffer.AsMemory(0, bytes), cancellationToken);
            copied += bytes;
            progress?.Invoke((int)(copied * 100 / release.Size));
        }
        if (copied != release.Size)
            throw new InvalidDataException("The downloaded setup is incomplete.");
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
            throw new CryptographicException("SHA-256 verification failed. This installer will not run.");
        await output.FlushAsync(cancellationToken);
    }
}
