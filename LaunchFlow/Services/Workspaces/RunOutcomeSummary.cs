namespace LaunchFlow.Services;

/// <summary>
/// Pure status/tray copy for a finished Run. Keeps MainWindow from owning the
/// wording rules for mixed failures vs a single sign-in attention reason.
/// </summary>
public static class RunOutcomeSummary
{
    public static bool HasProblems(int failedCount, int attentionCount, int stepRepairCount) =>
        failedCount > 0 || attentionCount > 0 || stepRepairCount > 0;

    public static string StatusLine(string failedLabel, int failedCount, string attentionLabel, int attentionCount, int stepRepairCount) =>
        $"{failedLabel}: {failedCount} · {attentionLabel}: {attentionCount} · Steps needing repair: {stepRepairCount}";

    public static string TrayDetail(
        int failedCount, int attentionCount, int stepRepairCount,
        string? singleAttentionReason, string? singleFailureReason = null)
    {
        // One clear sign-in reason alone; mixed outcomes keep the count summary.
        if (attentionCount == 1 && failedCount == 0 && stepRepairCount == 0 &&
            !string.IsNullOrWhiteSpace(singleAttentionReason))
            return singleAttentionReason;
        // One workflow failure with a concrete reason (e.g. Firefox automation).
        if (failedCount == 1 && attentionCount == 0 && stepRepairCount == 0 &&
            !string.IsNullOrWhiteSpace(singleFailureReason))
            return singleFailureReason;
        return $"{failedCount} workflow(s) failed; {attentionCount} sign-in(s) need attention; {stepRepairCount} step repair(s). Nothing is rerun automatically.";
    }

    public static string FinishedTray(int runCount, string? singleName) =>
        runCount == 1 && !string.IsNullOrWhiteSpace(singleName)
            ? $"Finished “{singleName}”."
            : $"Finished {runCount} workflows.";

    public static bool IsFirefoxAutomationFailure(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) &&
        reason.Contains("Firefox", StringComparison.OrdinalIgnoreCase) &&
        (reason.Contains("automation", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("Browser readiness", StringComparison.OrdinalIgnoreCase));
}
