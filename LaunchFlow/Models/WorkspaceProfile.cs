namespace LaunchFlow.Models;

/// <summary>
/// Named group of existing workflows. Stores only IDs, never credentials.
/// Missing/deleted workflows are ignored when the workspace runs.
/// </summary>
public sealed class WorkspaceProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New workspace";
    public List<Guid> WorkflowIds { get; set; } = new();

    public IEnumerable<WebsiteWorkflow> Resolve(Configuration configuration) =>
        WorkflowIds.Distinct().Select(id =>
            configuration.Workflows.FirstOrDefault(w => w.Id == id))
            .OfType<WebsiteWorkflow>().Where(w => w.Enabled);
}
