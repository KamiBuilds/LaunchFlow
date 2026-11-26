// Portable adapters for the extracted production startup handler; not native UI/storage.
namespace System.Windows
{
    public enum MessageBoxButton { OK, YesNo }
    public enum MessageBoxImage { Error, Warning, Information }
    public enum MessageBoxResult { OK, Yes, No }
    public static class MessageBox
    {
        public static List<string> Text { get; } = [];
        public static MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage image)
        { Text.Add(text); return MessageBoxResult.No; }
    }
}
namespace LaunchFlow
{
    public partial class App
    {
        // Cancellation is exercised in startup-exit; these handoff assertions use no exit request.
        private static bool StartupExitRequested => false;
        private static bool ShowStartupDialog(SetupWindow dialog) => dialog.ShowDialog() == true;
        private static bool ShowStartupDialog(UnlockWindow dialog) => dialog.ShowDialog() == true;
        private static bool ShowStartupDialog(SecretPromptWindow dialog) => dialog.ShowDialog() == true;
        private static bool TryRecoverIncompleteVault() => false;
        internal static bool StartForTest() => InitializeVault();
    }
    public sealed class SetupWindow
    {
        public static Queue<string?> Submissions { get; } = new();
        public static SetupWindow? Last { get; private set; }
        public string ChosenPassword { get; private set; } = "";
        public bool StartWithWindows => false;
        public bool RunAfterUnlock => false;
        public bool RunOnAppStart => false;
        public bool RunInTray => false;
        public bool? ShowDialog()
        {
            Last = this;
            var password = Submissions.Count == 0 ? null : Submissions.Dequeue();
            ChosenPassword = password ?? "";
            return password is not null;
        }
        internal string TakeChosenPassword() { var result = ChosenPassword; ChosenPassword = ""; return result; }
    }
    public sealed class SecretPromptWindow(string title, string text)
    {
        public string Secret => title + text;
        public bool? ShowDialog() => false;
    }
    public sealed class UnlockWindow
    {
        public bool? ShowDialog() => false;
        public bool ResetRequested => false;
        public string Password => "";
        public bool UsedWindowsHello => false;
    }
}
namespace LaunchFlow.Services
{
    public static class StartupStorageAdapter
    {
        public static bool HasRetainedCreationStage(this VaultDatabase _) => false;
    }
    public enum VaultStorageState { Absent, IncompleteOrUnsupported, LegacyUpgradeRequired, PresentNeedsAuthentication }
    public static class LegacyMigration
    {
        public static bool HasLegacy => false;
        public static bool VerifyOldMaster(string _) => false;
        public static (LaunchFlow.Models.Configuration Config, Dictionary<Guid, LaunchFlow.Models.SiteCredential> Secrets) ReadLegacyData() => (new(), []);
    }
    public static class VaultRecoveryService
    {
        public static bool Pending { get; set; }
        public static bool HasIncompletePreservation(string _) => Pending;
    }
    public static class ProfileVaultService { public static void UnsealAll() { } }
    public static class WindowsHelloService { public static void MarkPasswordVerified() { } }
    public static class VaultResetService { public static void ResetLocalVault(bool wipeProfiles) => throw new Exception("Reset must not run."); }
    public static class CrashLog { public static void Write(string _, Exception __) { } }
    public static class TaskSchedulerService { public static void Apply(bool _) { } }
    public static class StartupService { public static void Apply(bool _) { } }
    public sealed class StartupRegistrationService(Action<bool> first, Action<bool> second)
    { public void Apply(bool start, bool scheduler) { if (scheduler) first(start); else second(start); } }
}
