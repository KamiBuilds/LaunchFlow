using LaunchFlow.Models;
namespace LaunchFlow.Controllers;
/// <summary>Dispatcher-owned selection reference, edit revision and dirty acknowledgement.
/// Never replaces configuration, workspace or vault session identity.</summary>
public sealed class WorkflowEditorSession
{
    private WebsiteWorkflow? _selection;
    private long _revision;
    private bool _dirty;
    public WorkflowEditorSession(WebsiteWorkflow? selection = null) => _selection = selection;
    public WebsiteWorkflow? Selection => _selection;
    public long Revision => _revision;
    public bool IsDirty => _dirty;
    public void Select(WebsiteWorkflow? selection) => _selection = selection;
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
