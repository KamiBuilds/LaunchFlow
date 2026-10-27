using LaunchFlow.Models;
using LaunchFlow.Services;

internal static class WorkspaceCatalogTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("workspace save captures only enabled IDs and persists once", () =>
        {
            var a = new WebsiteWorkflow { Name = "A" };
            var b = new WebsiteWorkflow { Name = "B", Enabled = false };
            var config = new Configuration { Workflows = new() { a, b } };
            var writes = 0;
            var saved = WorkspaceCatalog.Save(config, "  Morning shift  ", () =>
            {
                writes++;
                Check(config.Workspaces.Count == 1 &&
                    config.Workspaces[0].WorkflowIds.SequenceEqual(new[] { a.Id }),
                    "persistence did not observe the intended saved profile");
            });
            Check(saved.Name == "Morning shift" && writes == 1 &&
                ReferenceEquals(config.Workspaces.Single(), saved),
                "new workspace save failed or duplicated profiles");
        });

        yield return ("workspace save failure rolls back new profile without changing workflows", () =>
        {
            var workflow = new WebsiteWorkflow();
            var config = new Configuration { Workflows = new() { workflow } };
            try
            {
                WorkspaceCatalog.Save(config, "Failing new workspace", () =>
                    throw new IOException("Synthetic persistence failure"));
                throw new InvalidOperationException("Persistence failure was ignored");
            }
            catch (IOException) { }
            Check(config.Workspaces.Count == 0 &&
                config.Workflows.Single() == workflow,
                "failed save leaked a new profile or modified workflows");
        });

        yield return ("workspace update failure restores existing identity, name and IDs", () =>
        {
            var original = new WebsiteWorkflow { Name = "Original" };
            var replacement = new WebsiteWorkflow { Name = "Replacement", Enabled = false };
            var profile = new WorkspaceProfile { Name = "NOC", WorkflowIds = new() { original.Id } };
            var config = new Configuration
            {
                Workflows = new() { original, replacement },
                Workspaces = new() { profile }
            };
            original.Enabled = false;
            replacement.Enabled = true;
            try
            {
                WorkspaceCatalog.Save(config, "noc", () =>
                    throw new IOException("Synthetic persistence failure"));
                throw new InvalidOperationException("Persistence failure was ignored");
            }
            catch (IOException) { }
            Check(config.Workspaces.Count == 1 &&
                ReferenceEquals(config.Workspaces[0], profile) &&
                profile.Name == "NOC" && profile.WorkflowIds.SequenceEqual(new[] { original.Id }),
                "failed overwrite changed the saved workspace");
            var saved = WorkspaceCatalog.Save(config, "NoC", () => { });
            Check(ReferenceEquals(saved, profile) &&
                saved.WorkflowIds.SequenceEqual(new[] { replacement.Id }) &&
                config.Workspaces.Count == 1,
                "successful retry failed to update the existing workspace");
        });

        yield return ("workspace selection rejects unsaved names and detached profile references", () =>
        {
            var stored = new WorkspaceProfile { Name = "Evening" };
            var other = new WorkspaceProfile { Name = "Evening" };
            var config = new Configuration { Workspaces = new() { stored } };
            Check(WorkspaceCatalog.IsCurrentSelection(config, stored, " EVENING  "),
                "matching workspace selection was rejected");
            Check(!WorkspaceCatalog.IsCurrentSelection(config, stored, "New workspace") &&
                !WorkspaceCatalog.IsCurrentSelection(config, other, "Evening") &&
                !WorkspaceCatalog.IsCurrentSelection(config, null, "Evening"),
                "stale workspace selection was treated as current");
        });

        yield return ("workspace name validation happens before any mutation or persistence", () =>
        {
            var config = new Configuration();
            var writes = 0;
            foreach (var invalid in new[] { " ", new string('X', 65) })
            {
                try
                {
                    WorkspaceCatalog.Save(config, invalid, () => writes++);
                    throw new InvalidOperationException("Invalid name was accepted");
                }
                catch (ArgumentException) { }
            }
            Check(config.Workspaces.Count == 0 && writes == 0,
                "invalid names caused persistence or collection mutation");
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
