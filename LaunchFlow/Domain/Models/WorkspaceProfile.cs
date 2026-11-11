using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaunchFlow.Models;

/// <summary>A named selection of existing workflow IDs; contains no credentials.</summary>
public sealed class WorkspaceProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New workspace";
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
    private List<Guid> _workflowIds = new();
    public List<Guid> WorkflowIds { get => _workflowIds; set => _workflowIds = value ?? new(); }

    public static WorkspaceProfile Capture(Configuration configuration, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 64)
            throw new ArgumentException("Enter a workspace name (1–64 characters).", nameof(name));
        var profile = configuration.Workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.Name, name, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new WorkspaceProfile();
            configuration.Workspaces.Add(profile);
        }
        profile.Name = name;
        profile.WorkflowIds = configuration.Workflows.Where(workflow => workflow.Enabled)
            .Select(workflow => workflow.Id).Distinct().ToList();
        return profile;
    }

    public IEnumerable<WebsiteWorkflow> Resolve(Configuration configuration) =>
        WorkflowIds.Distinct()
            .Select(id => configuration.Workflows.FirstOrDefault(workflow => workflow.Id == id))
            .OfType<WebsiteWorkflow>().Where(workflow => workflow.Enabled);
}
