using LaunchFlow.Services;

internal static class ExitCleanupWaitTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("slow cleanup refuses exit while underlying cleanup retains ownership", () =>
        {
            var gate = new ExitCompletionGate();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = gate.PrepareAsync(() => ExitCleanupWait.RequireAsync(release.Task,
                TimeSpan.FromMilliseconds(40), "test browser cleanup"));
            bool rejected = false;
            try { pending.GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) { rejected = ex.InnerException is TimeoutException; }
            if (!rejected || gate.Permitted || release.Task.IsCompleted)
                throw new InvalidOperationException("Timeout granted exit or cancelled the underlying browser cleanup.");
            release.TrySetResult();
            gate.PrepareAsync(() => Task.CompletedTask).GetAwaiter().GetResult();
            if (!gate.Permitted) throw new InvalidOperationException("A successful safe retry did not grant exit.");
        });
        yield return ("actual cleanup faults remain failures, not timeouts or exit permits", () =>
        {
            bool failed = false;
            try { ExitCleanupWait.RequireAsync(Task.FromException(new IOException("disk failure")),
                TimeSpan.FromSeconds(1), "test profile cleanup").GetAwaiter().GetResult(); }
            catch (IOException) { failed = true; }
            if (!failed) throw new InvalidOperationException("Cleanup exception was converted to successful exit.");
        });
    }
}
