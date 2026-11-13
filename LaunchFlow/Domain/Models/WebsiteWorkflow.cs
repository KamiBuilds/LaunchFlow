using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaunchFlow.Models;

public sealed class WebsiteWorkflow
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New workflow";
    public bool Enabled { get; set; } = true;
    public string StartUrl { get; set; } = "https://example.com";
    public string DestinationUrl { get; set; } = "";
    public string Browser { get; set; } = "Edge";
    public int MonitorIndex { get; set; }
    public string MonitorDeviceName { get; set; } = "";
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
    public string LoginMode { get; set; } = "Auto";
    /// <summary>Remembered Auto login shape: SamePage, EmailThenPassword, CustomSelectors, or empty.</summary>
    public string LoginRecipe { get; set; } = "";
    public bool AutoLogin { get; set; } = true;
    public string UsernameSelector { get; set; } = LoginFieldDefaults.UsernameSelector;
    public string PasswordSelector { get; set; } = LoginFieldDefaults.PasswordSelector;
    public string SubmitSelector { get; set; } = LoginFieldDefaults.SubmitSelector;
    public string CredentialOrigin { get; set; } = "";
    public string PasswordCredentialOrigin { get; set; } = "";
    public string UsernameContinueSelector { get; set; } = "";
    public string LoginSuccessSelector { get; set; } = "";
    public string LoginSuccessUrlContains { get; set; } = "";
    public int LoginWaitSeconds { get; set; } = 60;
    public string? KeepassEntryPath { get; set; }
    public List<WorkflowStep> Steps { get; set; } = new();

    [JsonIgnore]
    public HashSet<string> ApprovedLoginOrigins { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Persist as a list for System.Text.Json compatibility.
    [JsonPropertyName("ApprovedLoginOrigins")]
    public List<string> ApprovedLoginOriginsList
    {
        get => ApprovedLoginOrigins.ToList();
        set
        {
            ApprovedLoginOrigins = new HashSet<string>(value ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }
    }

    public WebsiteWorkflow CloneWithoutCredential()
    {
        return new WebsiteWorkflow
        {
            Id = Guid.NewGuid(),
            Name = Name + " (copy)",
            Enabled = false,
            StartUrl = StartUrl,
            DestinationUrl = DestinationUrl,
            Browser = Browser,
            MonitorIndex = MonitorIndex,
            MonitorDeviceName = MonitorDeviceName,
            LoginMode = LoginMode,
            LoginRecipe = LoginRecipe,
            AutoLogin = AutoLogin,
            UsernameSelector = UsernameSelector,
            PasswordSelector = PasswordSelector,
            SubmitSelector = SubmitSelector,
            // A duplicate has a new workflow identity, not inherited approval.
            CredentialOrigin = "",
            PasswordCredentialOrigin = "",
            UsernameContinueSelector = UsernameContinueSelector,
            LoginSuccessSelector = LoginSuccessSelector,
            LoginSuccessUrlContains = LoginSuccessUrlContains,
            LoginWaitSeconds = LoginWaitSeconds,
            KeepassEntryPath = null,
            Steps = Steps.Select(s => new WorkflowStep
            {
                Type = s.Type,
                Target = s.Target,
                Value = s.Value,
                Description = s.Description
            }).ToList(),
            ApprovedLoginOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
    }
}
