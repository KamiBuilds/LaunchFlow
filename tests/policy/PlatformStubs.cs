using LaunchFlow.Models;

namespace LaunchFlow.Services;

// Optional external process lookup is not launched by this Linux vault harness.
// Vault encryption, SQLite, transfer, policy and diagnostics are production code.
public static class KeePassXcService
{
    public static int Calls { get; private set; }
    public static SiteCredential? ReadEntry(string database, string entry, string? keyFile, string? password)
    {
        Calls++;
        throw new InvalidOperationException("External KeePass lookup is outside this harness.");
    }
}
