using System.Threading;

namespace LaunchFlow.Controllers;

/// <summary>
/// Dispatcher-owned Teach/Repair admission. Mirrors RunUiOperationController:
/// one lease owns the modeless teach lifetime; Run and Teach cannot overlap.
/// </summary>
public sealed class TeachUiOperationController
{
    private long _nextLease;
    private long _activeLease;
    private Task? _activeTask;

    public bool IsBusy => _activeLease != 0;
    public Task? ActiveTask => _activeTask;

    public bool TryBegin(out long lease)
    {
        lease = 0;
        if (IsBusy) return false;
        _activeLease = ++_nextLease;
        _activeTask = null;
        lease = _activeLease;
        return true;
    }

    public bool TrackTask(long lease, Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!Owns(lease) || _activeTask is not null) return false;
        _activeTask = task;
        return true;
    }

    public bool Owns(long lease) => lease != 0 && lease == _activeLease;

    public bool Complete(long lease)
    {
        if (!Owns(lease)) return false;
        _activeTask = null;
        _activeLease = 0;
        return true;
    }
}
