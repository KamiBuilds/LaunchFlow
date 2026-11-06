using LaunchFlow.Controllers;
using LaunchFlow.Models;

internal static class OperationOwnershipTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("UI run lease prevents a second admission before any token or modal prompt", () =>
        {
            var owner = new RunUiOperationController();
            Check(owner.TryBeginRun(out var first) && first > 0 && owner.IsRunning &&
                !owner.CanStop && owner.Cancellation is null && owner.ActiveTask is null,
                "First operation was not admitted before authentication");
            Check(!owner.TryBeginRun(out var duplicate) && duplicate == 0 &&
                owner.Owns(first), "Second modal run obtained a lease");
            Check(owner.Complete(first) && !owner.IsRunning, "Completed UI lease did not release");
        });
        yield return ("stale background callback lease cannot overwrite newer operation", () =>
        {
            var owner = new RunUiOperationController();
            Check(owner.TryBeginRun(out var old), "old lease");
            using var c1 = new CancellationTokenSource();
            Check(owner.AttachCancellation(old, c1) && owner.BeginStatus(old), "old status");
            var observed = owner.CaptureLeaseForCallback();
            Check(owner.IsStatusCurrent(observed), "current status denied");
            Check(owner.Complete(old) && owner.TryBeginRun(out var next), "new lease");
            using var c2 = new CancellationTokenSource();
            Check(owner.AttachCancellation(next, c2) && owner.BeginStatus(next) &&
                observed != owner.CaptureLeaseForCallback() &&
                !owner.IsStatusCurrent(observed) && owner.IsStatusCurrent(next),
                "old status callback authorized under new run");
            owner.Complete(next);
        });
        yield return ("stop and close revoke only the owned cancellation and do not replace engine task", () =>
        {
            var owner = new RunUiOperationController();
            Check(owner.TryBeginRun(out var first), "lease");
            using var token = new CancellationTokenSource();
            Check(owner.AttachCancellation(first, token), "token attached");
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Check(owner.TrackTask(first, pending.Task) &&
                ReferenceEquals(owner.ActiveTask, pending.Task), "real task not tracked");
            owner.RequestStop();
            Check(token.IsCancellationRequested && !owner.IsStatusCurrent(first) &&
                ReferenceEquals(owner.ActiveTask, pending.Task),
                "stop lost task or retained status");
            pending.SetResult();
            Check(owner.Complete(first) && !owner.IsRunning, "completion failed");
            Check(owner.TryBeginRun(out var second), "second run failed");
            using var fresh = new CancellationTokenSource();
            Check(owner.AttachCancellation(second, fresh) &&
                !owner.Complete(first) && !fresh.IsCancellationRequested &&
                ReferenceEquals(owner.Cancellation, fresh), "stale completion affected new run");
            owner.Complete(second);
        });
        yield return ("editor same GUID imported replacement cannot reuse selection ownership", () =>
        {
            var a = new WebsiteWorkflow { Id = Guid.NewGuid(), Name = "Before" };
            var b = new WebsiteWorkflow { Id = a.Id, Name = "Replacement" };
            var editor = new WorkflowEditorSession(a);
            editor.MarkEdited();
            var rev = editor.Revision;
            Check(editor.IsCurrent(a, rev), "first selection missing");
            editor.Select(b);
            Check(!editor.IsCurrent(a, rev) && editor.IsCurrent(b, rev),
                "same-GUID replacement borrowed stale workflow identity");
        });
        yield return ("editor late edit and rejected save cannot acknowledge earlier revision", () =>
        {
            var editor = new WorkflowEditorSession(new WebsiteWorkflow());
            editor.MarkEdited();
            var captured = editor.Revision;
            editor.MarkEdited();
            Check(!editor.TryAcknowledge(captured) && editor.IsDirty,
                "late edit was incorrectly marked saved");
            editor.MarkFailed();
            Check(editor.IsDirty && editor.TryAcknowledge(editor.Revision) &&
                !editor.IsDirty, "failed state could not retry and commit");
        });
        yield return ("editor selection cannot publish past an intervening selection change", () =>
        {
            var selected = new WebsiteWorkflow { Id = Guid.NewGuid() };
            var other = new WebsiteWorkflow { Id = Guid.NewGuid() };
            var editor = new WorkflowEditorSession(selected);
            var revision = editor.Revision;
            editor.Select(other);
            Check(!editor.IsCurrent(selected, revision), "stale selection accepted");
            editor.Select(selected);
            editor.MarkEdited();
            Check(!editor.IsCurrent(selected, revision), "stale edit revision accepted");
        });
    }
    private static void Check(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }
}
