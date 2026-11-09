namespace LaunchFlow.Models;

/// <summary>
/// The sole model-assignment and normalization contract for website editor values.
/// The WPF adapters determine when to capture each stage: navigation may publish
/// leading fields before browser validation and must raise its AutoLogin UI event
/// before reading final selectors. Durable Save applies to a detached candidate.
/// </summary>
public static class WebsiteEditMapper
{
    public static string NormalizeName(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "Unnamed website" : raw.Trim();

    public static string NormalizeText(string? raw) => raw?.Trim() ?? "";

    public static int NormalizeWait(string? raw) =>
        int.TryParse(raw, out var seconds) ? Math.Clamp(seconds, 5, 180) : 60;

    public static void ApplyLeading(WebsiteWorkflow workflow, bool enabled,
        string name, string startUrl, string destinationUrl)
    {
        workflow.Enabled = enabled;
        workflow.Name = NormalizeName(name);
        workflow.StartUrl = NormalizeText(startUrl);
        workflow.DestinationUrl = NormalizeText(destinationUrl);
    }

    public static void ApplyBrowser(WebsiteWorkflow workflow, string browser,
        bool replaceMonitor, int monitorIndex, string monitorDeviceName)
    {
        if (string.IsNullOrWhiteSpace(browser))
            throw new InvalidOperationException("Choose an installed browser before saving this website.");
        workflow.Browser = browser;
        if (replaceMonitor)
        {
            workflow.MonitorIndex = monitorIndex;
            workflow.MonitorDeviceName = monitorDeviceName;
        }
    }

    public static void ApplyLogin(WebsiteWorkflow workflow, string mode,
        string usernameContinueSelector, string passwordCredentialOrigin, bool autoLogin)
    {
        workflow.LoginMode = mode;
        workflow.UsernameContinueSelector = NormalizeText(usernameContinueSelector);
        workflow.PasswordCredentialOrigin = NormalizeText(passwordCredentialOrigin);
        workflow.AutoLogin = autoLogin;
    }

    public static void ApplyDetails(WebsiteWorkflow workflow,
        string keepassEntryPath, string usernameSelector, string passwordSelector,
        string submitSelector, string credentialOrigin, string loginSuccessSelector,
        string loginSuccessUrlContains, int loginWaitSeconds)
    {
        workflow.KeepassEntryPath = NormalizeText(keepassEntryPath);
        workflow.UsernameSelector = NormalizeText(usernameSelector);
        workflow.PasswordSelector = NormalizeText(passwordSelector);
        workflow.SubmitSelector = NormalizeText(submitSelector);
        workflow.CredentialOrigin = NormalizeText(credentialOrigin);
        workflow.LoginSuccessSelector = NormalizeText(loginSuccessSelector);
        workflow.LoginSuccessUrlContains = NormalizeText(loginSuccessUrlContains);
        workflow.LoginWaitSeconds = Math.Clamp(loginWaitSeconds, 5, 180);
    }

    public static void Apply(WebsiteWorkflow workflow, WebsiteEditDraft values)
    {
        ApplyLeading(workflow, values.Enabled, values.Name, values.StartUrl, values.DestinationUrl);
        ApplyBrowser(workflow, values.Browser, values.ReplaceMonitor, values.MonitorIndex,
            values.MonitorDeviceName);
        ApplyLogin(workflow, values.LoginMode, values.UsernameContinueSelector,
            values.PasswordCredentialOrigin, values.AutoLogin);
        ApplyDetails(workflow, values.KeepassEntryPath, values.UsernameSelector,
            values.PasswordSelector, values.SubmitSelector, values.CredentialOrigin,
            values.LoginSuccessSelector, values.LoginSuccessUrlContains, values.LoginWaitSeconds);
    }
}
