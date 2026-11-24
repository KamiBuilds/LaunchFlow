namespace LaunchFlow.Services;

public sealed partial class VaultDatabase
{
    // Platform-only implementation; core storage harnesses may omit the diagnostic hook entirely.
    partial void TraceCreationStage(string stage)
    {
        try
        {
            // Reject numeric enum parsing, case variants and non-creation events.
            if (stage is null || !stage.StartsWith("VaultCreate", StringComparison.Ordinal)) return;
            if (Enum.TryParse<StartupDiagnostics.Stage>(stage, ignoreCase: false, out var value) &&
                Enum.IsDefined(value) && value.ToString() == stage)
                StartupDiagnostics.Record(value);
        }
        catch { /* Logging must never replace an operational or cleanup failure. */ }
    }
}
