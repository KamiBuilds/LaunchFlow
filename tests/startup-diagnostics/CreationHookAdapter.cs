namespace LaunchFlow.Services;
// Test-only declaration models the storage-owned optional hook; no storage is executed by this adapter.
public sealed partial class VaultDatabase
{
    partial void TraceCreationStage(string stage);
    internal void TestCreationStage(string stage) => TraceCreationStage(stage);
}
