namespace LaunchFlow.Services;

/// <summary>
/// Ensures exactly one Windows startup registration mechanism is enabled.
/// Platform integration is supplied explicitly, allowing deterministic tests
/// without changing a user's Task Scheduler or Run registry key.
/// </summary>
public sealed class StartupRegistrationService
{
    private readonly Action<bool> _taskScheduler;
    private readonly Action<bool> _runKey;

    public StartupRegistrationService(Action<bool> taskScheduler, Action<bool> runKey)
    {
        _taskScheduler = taskScheduler ?? throw new ArgumentNullException(nameof(taskScheduler));
        _runKey = runKey ?? throw new ArgumentNullException(nameof(runKey));
    }

    public void Apply(bool startWithWindows, bool preferTaskScheduler)
    {
        // Disable the mechanism we're *not* using first, so a successful
        // reconfiguration never schedules duplicate application launches.
        if (preferTaskScheduler)
        {
            _runKey(false);
            _taskScheduler(startWithWindows);
        }
        else
        {
            _taskScheduler(false);
            _runKey(startWithWindows);
        }
    }
}
