namespace LaunchFlow.Services;

/// <summary>
/// Pure debounced-save state machine. No UI thread, credentials, browser,
/// configuration database or OS dependency. One failed write is not retried
/// continuously; a new user edit or an explicit Save can retry safely.
/// </summary>
public sealed class AutoSaveSchedule
{
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(900);
    private readonly TimeSpan _delay;
    private DateTimeOffset _dueUtc;
    private bool _attempted;

    public AutoSaveSchedule(TimeSpan? delay = null)
    {
        _delay = delay ?? DefaultDelay;
        if (_delay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay));
    }

    public bool HasPendingChanges { get; private set; }

    public void Changed(DateTimeOffset nowUtc)
    {
        HasPendingChanges = true;
        _attempted = false;
        _dueUtc = nowUtc + _delay;
    }

    public bool Ready(DateTimeOffset nowUtc, bool canPersist) =>
        HasPendingChanges && !_attempted && canPersist && nowUtc >= _dueUtc;

    public void Attempted()
    {
        // No timer-driven retry storm when an invalid field or disk error is
        // encountered; further editing resets this and starts a fresh delay.
        _attempted = true;
    }

    public void Saved()
    {
        HasPendingChanges = false;
        _attempted = false;
        _dueUtc = default;
    }
}
