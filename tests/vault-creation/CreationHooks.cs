namespace LaunchFlow.Services;

// Only this portable executable implements the operation fault seam. No
// filesystem, SQLite, encryption, KDF, serialization or commit is substituted.
public sealed partial class VaultDatabase
{
    internal List<string> CreationStages { get; } = new();
    internal Action<string>? TestCreationOperation { get; set; }
    internal static Action<string, byte[]>? TestSensitiveBuffer { get; set; }
    static partial void SensitiveBufferForTesting(string kind, byte[] buffer) => TestSensitiveBuffer?.Invoke(kind, buffer);
    partial void TraceCreationStage(string stage) => CreationStages.Add(stage);
    partial void CreationOperationForTesting(string operation) => TestCreationOperation?.Invoke(operation);
}
