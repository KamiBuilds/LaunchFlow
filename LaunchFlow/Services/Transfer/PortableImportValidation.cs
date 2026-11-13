using System.IO;
using LaunchFlow.Models;

namespace LaunchFlow.Services;

/// <summary>
/// Post-decrypt checks for portable .launchflow packages. Credentials and
/// website fields never become SQL; this still rejects hostile shapes that
/// would break later runs or smuggle non-HTTP URLs into automation.
/// </summary>
public static class PortableImportValidation
{
    private const int MaxWorkflows = 500;
    private const int MaxWorkspaces = 500;
    private const int MaxStepsPerWorkflow = 500;
    private const int MaxNameLength = 200;
    private const int MaxSelectorLength = 4096;
    private const int MaxDescriptionLength = 500;
    private static readonly HashSet<string> AllowedStepTypes = new(StringComparer.Ordinal)
    {
        // Fill is refused on import: recorded/typed values must not travel in backups.
        "Navigate", "Click", "Wait", "Press", "AssertVisible", "WaitForURL"
    };
    private static readonly HashSet<string> AllowedPressKeys = new(StringComparer.Ordinal)
    {
        "Enter", "Tab", "Escape", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"
    };

    public static void Validate(PortableExport.PortablePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(package.Config);
        ArgumentNullException.ThrowIfNull(package.Credentials);
        ArgumentNullException.ThrowIfNull(package.Config.Workflows);

        if (package.Config.Workflows.Count > MaxWorkflows)
            throw new InvalidDataException("Import has too many websites.");
        if (package.Config.Workspaces.Count > MaxWorkspaces)
            throw new InvalidDataException("Import has too many workspaces.");

        var ids = new HashSet<Guid>();
        foreach (var workflow in package.Config.Workflows)
        {
            if (workflow is null)
                throw new InvalidDataException("Import contains a null website.");
            if (workflow.Id == Guid.Empty || !ids.Add(workflow.Id))
                throw new InvalidDataException("Import websites must each have a unique id.");
            if (string.IsNullOrWhiteSpace(workflow.Name) || workflow.Name.Length > MaxNameLength)
                throw new InvalidDataException("Import website name is missing or too long.");
            RequireHttpUrl(workflow.StartUrl, "website start address");
            if (!string.IsNullOrWhiteSpace(workflow.DestinationUrl))
                RequireHttpUrl(workflow.DestinationUrl, "website destination");
            RequireOptionalHttpsOrigin(workflow.CredentialOrigin, "trusted login origin");
            RequireOptionalHttpsOrigin(workflow.PasswordCredentialOrigin, "trusted password page");
            RequireBounded(workflow.UsernameSelector, "email selector");
            RequireBounded(workflow.PasswordSelector, "password selector");
            RequireBounded(workflow.SubmitSelector, "sign-in button selector");
            RequireBounded(workflow.UsernameContinueSelector, "continue-button selector");
            RequireBounded(workflow.LoginSuccessSelector, "signed-in selector");
            if (workflow.LoginWaitSeconds is < 5 or > 600)
                throw new InvalidDataException("Import sign-in wait is out of range.");
            if (workflow.Steps is null)
                throw new InvalidDataException("Import website steps list is missing.");
            if (workflow.Steps.Count > MaxStepsPerWorkflow)
                throw new InvalidDataException("Import website has too many recorded actions.");
            foreach (var step in workflow.Steps)
                ValidateStep(step);
            if (workflow.ApprovedLoginOrigins is not null)
            {
                foreach (var origin in workflow.ApprovedLoginOrigins)
                    RequireOptionalHttpsOrigin(origin, "approved login origin");
            }
        }

        // Workspaces are loaded directly into the UI after import. Reject
        // malformed entries rather than persisting objects that later crash
        // workspace selection. Nonempty legacy references to removed websites
        // remain compatible: WorkspaceProfile.Resolve already ignores them.
        var workspaceIds = new HashSet<Guid>();
        foreach (var workspace in package.Config.Workspaces)
        {
            if (workspace is null)
                throw new InvalidDataException("Import contains a null workspace.");
            if (workspace.Id == Guid.Empty || !workspaceIds.Add(workspace.Id))
                throw new InvalidDataException("Import workspaces must each have a unique id.");
            if (string.IsNullOrWhiteSpace(workspace.Name) || workspace.Name.Length > 64)
                throw new InvalidDataException("Import workspace name is missing or too long.");
            if (workspace.WorkflowIds.Count > MaxWorkflows ||
                workspace.WorkflowIds.Contains(Guid.Empty))
                throw new InvalidDataException("Import workspace contains invalid website references.");
        }

        foreach (var pair in package.Credentials)
        {
            if (pair.Key == Guid.Empty)
                throw new InvalidDataException("Import credentials contain an empty website id.");
            if (!ids.Contains(pair.Key))
                throw new InvalidDataException("Import credentials refer to a website missing from the backup.");
            if (pair.Value is null)
                throw new InvalidDataException("Import credentials contain a null entry.");
            if (pair.Value.Username is { Length: > 2048 } || pair.Value.Password is { Length: > 4096 })
                throw new InvalidDataException("Import credential fields are too large.");
        }
    }

    private static void ValidateStep(WorkflowStep? step)
    {
        if (step is null)
            throw new InvalidDataException("Import contains a null recorded action.");
        if (!AllowedStepTypes.Contains(step.Type ?? ""))
            throw new InvalidDataException("Import contains an unsupported recorded action type.");
        if (step.Description is { Length: > MaxDescriptionLength })
            throw new InvalidDataException("Import recorded-action description is too long.");
        switch (step.Type)
        {
            case "Navigate":
            case "WaitForURL":
                RequireHttpUrl(step.Target, "recorded page address");
                break;
            case "Click":
            case "AssertVisible":
                RequireBounded(step.Target, "recorded selector");
                if (string.IsNullOrWhiteSpace(step.Target))
                    throw new InvalidDataException("Import recorded action is missing its target.");
                break;
            case "Press":
                if (!AllowedPressKeys.Contains(step.Target ?? ""))
                    throw new InvalidDataException("Import key action is not allowed.");
                break;
            case "Wait":
                if (!int.TryParse(step.Target, out var ms) || ms is < 0 or > 120_000)
                    throw new InvalidDataException("Import wait action is out of range.");
                break;
        }
        // Never keep typed payload fields from a portable package.
        if (!string.IsNullOrEmpty(step.Value))
            throw new InvalidDataException("Import recorded actions cannot carry typed values.");
    }

    private static void RequireHttpUrl(string? raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 2048)
            throw new InvalidDataException("Import " + label + " is missing or too long.");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Import " + label + " must be a plain http(s) address.");
    }

    private static void RequireOptionalHttpsOrigin(string? raw, string label)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        RequireHttpUrl(raw, label);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Import " + label + " must use HTTPS.");
    }

    private static void RequireBounded(string? raw, string label)
    {
        if (raw is { Length: > MaxSelectorLength })
            throw new InvalidDataException("Import " + label + " is too long.");
    }
}
