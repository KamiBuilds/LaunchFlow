namespace LaunchFlow.Services;

/// <summary>Stop awaiting an unresponsive cleanup without cancelling or granting an exit permit.</summary>
public static class ExitCleanupWait
{
    public static async Task RequireAsync(Task cleanup, TimeSpan budget, string operation)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));
        try { await cleanup.WaitAsync(budget); }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                operation + " is still in progress. No browser-profile sealing or exit was authorized; retry when cleanup finishes.", ex);
        }
    }
}
