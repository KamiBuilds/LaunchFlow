namespace LaunchFlow.Services;

/// <summary>
/// Pure status/tray copy for a finished Run. Keeps MainWindow from owning the
/// wording rules for mixed failures vs a single sign-in attention reason.
/// Wording stays calm — never alarmist — for non-IT users.
/// </summary>
public static class RunOutcomeSummary
{
    public static bool HasProblems(int failedCount, int attentionCount, int stepRepairCount) =>
        failedCount > 0 || attentionCount > 0 || stepRepairCount > 0;

    public static string StatusLine(string failedLabel, int failedCount, string attentionLabel, int attentionCount, int stepRepairCount)
    {
        if (failedCount == 0 && attentionCount == 1 && stepRepairCount == 0)
            return "Almost there · check the browser window";
        if (failedCount == 1 && attentionCount == 0 && stepRepairCount == 0)
            return "Couldn't finish this time · try again when ready";
        if (failedCount == 0 && attentionCount == 0 && stepRepairCount > 0)
            return "One click step needs a quick look";
        return $"{failedLabel}: {failedCount} · {attentionLabel}: {attentionCount} · Steps to review: {stepRepairCount}";
    }

    public static string TrayDetail(
        int failedCount, int attentionCount, int stepRepairCount,
        string? singleAttentionReason, string? singleFailureReason = null)
    {
        // One clear sign-in reason alone; mixed outcomes keep the count summary.
        if (attentionCount == 1 && failedCount == 0 && stepRepairCount == 0 &&
            !string.IsNullOrWhiteSpace(singleAttentionReason))
            return Soften(singleAttentionReason);
        // One workflow failure with a concrete reason (e.g. Firefox automation).
        if (failedCount == 1 && attentionCount == 0 && stepRepairCount == 0 &&
            !string.IsNullOrWhiteSpace(singleFailureReason))
            return Soften(singleFailureReason);
        return "Something didn’t finish this time. Your saved sites and passwords are fine — try again when you’re ready.";
    }

    public static string FinishedTray(int runCount, string? singleName) =>
        runCount == 1 && !string.IsNullOrWhiteSpace(singleName)
            ? $"Finished “{singleName}”."
            : $"Finished {runCount} websites.";

    public static bool IsFirefoxAutomationFailure(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) &&
        reason.Contains("Firefox", StringComparison.OrdinalIgnoreCase) &&
        (reason.Contains("automation", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("Browser readiness", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("Check browsers", StringComparison.OrdinalIgnoreCase) ||
         reason.Contains("special Firefox", StringComparison.OrdinalIgnoreCase));

    private static string Soften(string reason)
    {
        // Keep the useful tip, drop harsh framing if present.
        return reason.Trim();
    }
}
