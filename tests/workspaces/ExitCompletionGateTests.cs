using LaunchFlow.Services;

internal static class ExitCompletionGateTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("stalled browser cleanup cannot grant early exit or duplicate operations", () =>
        {
            var gate = new ExitCompletionGate();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var first = gate.PrepareAsync(() => { calls++; return release.Task; });
            var duplicate = gate.PrepareAsync(() => { calls++; return Task.CompletedTask; });
            Check(!gate.Permitted && !first.IsCompleted && ReferenceEquals(first, duplicate) && calls == 1,
                "an exit bypassed the pending cleanup");
            release.SetResult();
            first.GetAwaiter().GetResult();
            Check(gate.Permitted, "clean closure was not permitted");
        });
        yield return ("failed cleanup refuses exit and allows retry", () =>
        {
            var gate = new ExitCompletionGate();
            bool rejected = false;
            try { gate.PrepareAsync(() => Task.FromException(new IOException("synthetic browser error")))
                .GetAwaiter().GetResult(); }
            catch (IOException) { rejected = true; }
            Check(rejected && !gate.Permitted, "failed cleanup authorized closure");
            gate.PrepareAsync(() => Task.CompletedTask).GetAwaiter().GetResult();
            Check(gate.Permitted, "recovery could not acquire a fresh permit");
        });
        yield return ("cancelled cleanup refuses exit", () =>
        {
            var gate = new ExitCompletionGate();
            bool rejected = false;
            try { gate.PrepareAsync(() => Task.FromCanceled(new CancellationToken(true)))
                .GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { rejected = true; }
            Check(rejected && !gate.Permitted, "cancelled cleanup authorized closure");
        });
        yield return ("late unsaved edits can revoke a completed permit", () =>
        {
            var gate = new ExitCompletionGate();
            gate.PrepareAsync(() => Task.CompletedTask).GetAwaiter().GetResult();
            gate.RevokePermit();
            Check(!gate.Permitted, "rejected close retained old permission");
            gate.PrepareAsync(() => Task.CompletedTask).GetAwaiter().GetResult();
            Check(gate.Permitted, "fresh cleanup could not authorize a later close");
        });
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
