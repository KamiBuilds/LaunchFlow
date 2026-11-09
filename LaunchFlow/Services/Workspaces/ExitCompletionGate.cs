namespace LaunchFlow.Services;

/// <summary>
/// Owns a single asynchronous cleanup and grants closure only after success.
/// Cancellation and exceptions never grant a close permit.
/// </summary>
public sealed class ExitCompletionGate
{
    private Task? _pending;
    public bool Permitted { get; private set; }

    public Task PrepareAsync(Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (Permitted) return Task.CompletedTask;
        if (_pending is { IsCompleted: false }) return _pending;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion.Task;
        async Task DrainAsync()
        {
            try
            {
                await cleanup();
                Permitted = true;
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                Permitted = false;
                completion.TrySetException(ex);
            }
        }
        _ = DrainAsync();
        return completion.Task;
    }

    public void RevokePermit()
    {
        if (_pending is { IsCompleted: false })
            throw new InvalidOperationException("The active shutdown has not finished.");
        Permitted = false;
        _pending = null;
    }
}
