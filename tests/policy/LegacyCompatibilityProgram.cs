using LaunchFlow.Services;

if (args.Length != 1 || (args[0] != "--compat-create" && args[0] != "--compat-read"))
    throw new ArgumentException("Legacy harness accepts only --compat-create or --compat-read.");

return CompatibilityTests.Run(args[0]);

internal static class VaultTests
{
    internal static void Check(bool condition, string message = "Legacy compatibility assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
