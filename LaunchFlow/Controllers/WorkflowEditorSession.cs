using LaunchFlow.Models;
namespace LaunchFlow.Controllers;
/// <summary>Dispatcher-owned selection reference, edit revision and dirty acknowledgement.
/// Never replaces configuration, workspace or vault session identity.</summary>
public sealed class WorkflowEditorSession
{
    private WebsiteWorkflow? _selection;
    private long _revision;
    private long _selectionEpoch;
    private bool _dirty;
    public readonly record struct OwnershipToken(WebsiteWorkflow? Selection, long EditRevision, long SelectionEpoch);
    public OwnershipToken CaptureOwnership() => new(_selection, _revision, _selectionEpoch);
    public bool IsCurrent(OwnershipToken owner) =>
        ReferenceEquals(_selection, owner.Selection) &&
        _revision == owner.EditRevision && _selectionEpoch == owner.SelectionEpoch;
    public bool TryAcknowledge(OwnershipToken owner)
    {
        if (!IsCurrent(owner)) return false;
        _dirty = false; return true;
    }
    public WorkflowEditorSession(WebsiteWorkflow? selection = null) => _selection = selection;
    public WebsiteWorkflow? Selection => _selection;
    public long Revision => _revision;
    public bool IsDirty => _dirty;
    public void Select(WebsiteWorkflow? selection)
    {
        if (ReferenceEquals(_selection, selection)) return;
        _selection = selection;
        checked { _selectionEpoch++; }
    }
    public void MarkEdited() { checked { _revision++; } _dirty = true; }
    public void MarkFailed() => _dirty = true;
    public bool IsCurrent(WebsiteWorkflow? selection, long revision) =>
        ReferenceEquals(_selection, selection) && _revision == revision;
    public bool TryAcknowledge(long revision)
    {
        if (_revision != revision) return false;
        _dirty = false; return true;
    }
}
