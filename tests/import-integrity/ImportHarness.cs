using LaunchFlow.Models;
using LaunchFlow.Services;

namespace LaunchFlow
{
    public sealed class FixtureControl
    {
        public object? ItemsSource { get; set; }
        public object? SelectedItem { get; set; }
        public int SelectedIndex { get; set; }
        public bool? IsChecked { get; set; }
        public string Text { get; set; } = "";
        public void Clear() => Text = "";
    }

    public sealed class FakeRunner { public bool IsRunning { get; set; } }

    public sealed partial class MainWindow
    {
        public Configuration _configuration;
        public long _workspaceRevision;
        public bool _windowsLocked, _vaultLockInProgress, _exiting, _loadingEditor;
        public string _activeShortcut = "Ctrl+M";
        public string? _keepassMasterPassword;
        public readonly FakeRunner _runner = new();
        public readonly FixtureControl SettingsTabs = new(), MainTabs = new();
        public readonly FixtureControl WorkflowList = new(), WorkspaceCombo = new(), WorkspaceNameBox = new();
        public readonly FixtureControl LanguageCombo = new(), StartupCheck = new(), UnlockCheck = new();
        public readonly FixtureControl LaunchCheck = new(), TrayCheck = new(), NotifyCheck = new();
        public readonly FixtureControl SealProfilesCheck = new(), TaskSchedulerCheck = new();
        public readonly FixtureControl DelayBox = new(), VaultTimeoutBox = new(), ShortcutBox = new();
        public readonly FixtureControl KeePassDbBox = new(), KeePassKeyBox = new(), UpdateFeedBox = new();
        public bool Imported { get; private set; }
        public readonly List<string> Logs = new();

        public MainWindow(Configuration configuration) => _configuration = configuration;
        public void Import(string path) => ImportLaunchFlowFile(path);
        private bool RequireMasterPassword(bool forcePrompt = false) => VaultDatabase.Current.IsUnlocked;
        private void ApplyUiLanguage() { }
        private void RefreshKeePassStatus() { }
        private void RefreshWorkflowList(WebsiteWorkflow? workflow) { }
        private void MarkSaved() => Imported = true;
        private void AppendLog(string message) => Logs.Add(message);
    }

    public sealed class SecretPromptWindow
    {
        public object? Owner { get; init; }
        public string Secret => "Synthetic!Backup-Export8";
        public SecretPromptWindow(string title, string message) { }
        public bool? ShowDialog()
        {
            Hooks.PasswordPrompt?.Invoke();
            return true;
        }
    }

    public static class Hooks
    {
        public static Action? PasswordPrompt;
        public static Action? ConfirmImport;
        public static void Reset() { PasswordPrompt = null; ConfirmImport = null; }
    }
}

namespace System.Windows
{
    public enum MessageBoxButton { OK, YesNo }
    public enum MessageBoxImage { Warning, Information, Error }
    public enum MessageBoxResult { Yes, No, OK }
    public static class MessageBox
    {
        public static MessageBoxResult Show(object owner, string message, string title,
            MessageBoxButton button, MessageBoxImage image)
        {
            if (title == "Confirm import")
            {
                LaunchFlow.Hooks.ConfirmImport?.Invoke();
                return MessageBoxResult.Yes;
            }
            return MessageBoxResult.OK;
        }
        public static MessageBoxResult Show(object owner, string message, string title) =>
            MessageBoxResult.OK;
    }
}
