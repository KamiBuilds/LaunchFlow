using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaunchFlow.Models;

public sealed class Configuration
{
    public List<WebsiteWorkflow> Workflows { get; set; } = new();
    private List<WorkspaceProfile> _workspaces = new();
    public List<WorkspaceProfile> Workspaces { get => _workspaces; set => _workspaces = value ?? new(); }
    public string UiLanguage { get; set; } = "auto";
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
    public bool StartWithWindows { get; set; }
    public bool RunOnUnlock { get; set; }
    public bool RunOnAppStart { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public int UnlockDelaySeconds { get; set; } = 5;
    public int VaultLockMinutes { get; set; } = 10;
    public string GlobalShortcut { get; set; } = "Ctrl+M";
    public bool ShortcutTipShown { get; set; }
    public bool TrayNotificationsEnabled { get; set; } = true;
    public bool WelcomeChecklistShown { get; set; }
    public bool DiagnosticsLogEnabled { get; set; }
    public bool SealBrowserProfilesOnLock { get; set; } = true;
    public bool PreferTaskSchedulerAutostart { get; set; }
    public bool WindowsHelloUnlockEnabled { get; set; }
    public string? UpdateFeedUrl { get; set; }
    public string? KeePassDatabasePath { get; set; }
    public string? KeePassKeyFilePath { get; set; }
}
