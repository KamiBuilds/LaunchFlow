using System.Threading;
namespace LaunchFlow.Controllers;
/// <summary>Dispatcher-owned UI admission, status, cancellation and real engine Task.
/// Browser sessions and vault authorization remain owned by their existing services.
/// Only CaptureLeaseForCallback may be called outside the dispatcher.</summary>
public sealed class RunUiOperationController
{
    private long _nextLease, _activeLease, _publishedLease;
    private bool _statusActive;
    private CancellationTokenSource? _cancellation;
    private Task? _engineTask;
    public bool IsRunning => _activeLease != 0;
    public bool CanStop => IsRunning && _cancellation is { IsCancellationRequested: false };
    public bool IsCancellationRequested => _cancellation?.IsCancellationRequested == true;
    public CancellationTokenSource? Cancellation => _cancellation;
    public Task? ActiveTask => _engineTask;
    public bool TryBeginRun(out long lease)
    {
        lease = 0;
        if (IsRunning) return false;
        _activeLease = ++_nextLease;
        _statusActive = false;
        _cancellation = null;
        _engineTask = null;
        Volatile.Write(ref _publishedLease, _activeLease);
        lease = _activeLease;
        return true;
    }
    public bool AttachCancellation(long lease, CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        if (!Owns(lease) || _cancellation is not null) return false;
        _cancellation = cancellation; return true;
    }
    public bool TrackTask(long lease, Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!Owns(lease) || _cancellation is null) return false;
        _engineTask = task; return true;
    }
    public bool BeginStatus(long lease)
    {
        if (!Owns(lease) || _cancellation is null) return false;
        _statusActive = true; return true;
    }
    public void EndStatus(long lease) { if (Owns(lease)) _statusActive = false; }
    public void RequestStop() => _cancellation?.Cancel();
    public long CaptureLeaseForCallback() => Interlocked.Read(ref _publishedLease);
    public bool IsStatusCurrent(long lease) =>
        Owns(lease) && _statusActive && _cancellation is { IsCancellationRequested: false };
    public bool Owns(long lease) => lease != 0 && lease == _activeLease;
    public bool Complete(long lease)
    {
        if (!Owns(lease)) return false;
        _statusActive = false; _engineTask = null; _cancellation = null; _activeLease = 0;
        Volatile.Write(ref _publishedLease, 0);
        return true;
    }
}
