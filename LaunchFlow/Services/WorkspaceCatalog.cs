using LaunchFlow.Models;

namespace LaunchFlow.Services;

public static class WorkspaceCatalog
{
    public static WorkspaceProfile? FindByName(Configuration configuration, string name)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.Workspaces.FirstOrDefault(profile =>
            string.Equals(profile.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCurrentSelection(
        Configuration configuration, WorkspaceProfile? selected, string? enteredName)
    {
        return selected is not null &&
            configuration.Workspaces.Contains(selected) &&
            string.Equals(selected.Name, enteredName?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool ConfirmationStillValid(
        Configuration configuration, string name, WorkspaceProfile? expectedTarget,
        long expectedRevision, long currentRevision, IReadOnlyList<Guid> enabledWorkflowIds)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(enabledWorkflowIds);
        return expectedRevision == currentRevision &&
            ReferenceEquals(FindByName(configuration, name), expectedTarget) &&
            configuration.Workflows.Where(workflow => workflow.Enabled)
                .Select(workflow => workflow.Id).SequenceEqual(enabledWorkflowIds);
    }

    public static WorkspaceProfile Save(Configuration configuration, string name, Action persist)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(name);

        name = name.Trim();
        if (name.Length is < 1 or > 64)
            throw new ArgumentException("Enter a workspace name (1–64 characters).", nameof(name));

        var existing = FindByName(configuration, name);
        var originalName = existing?.Name;
        var originalIds = existing?.WorkflowIds.ToList();
        var profile = WorkspaceProfile.Capture(configuration, name);
        try
        {
            persist();
            return profile;
        }
        catch
        {
            if (existing is null)
                configuration.Workspaces.Remove(profile);
            else
            {
                existing.Name = originalName!;
                existing.WorkflowIds = originalIds!;
            }
            throw;
        }
    }
}
