using LaunchFlow.Models;
using LaunchFlow.Services;

namespace LaunchFlow
{
    public partial class MainWindow : System.Windows.Window
    {
        private bool _vaultLockInProgress = false, _windowsLocked = false, _exiting = false, _vaultCleanupFailed = false;
        private readonly RunAdapter _runUi = new();
        private readonly TeachAdapter _teachUi = new();
        private readonly MasterPasswordService _master = new();
        private readonly HelloAdapter _helloUnlock = new();
        private readonly Configuration _configuration = new();
        private readonly StoreAdapter _store = new();
        private bool RequireMasterPassword() => VaultDatabase.Current.IsUnlocked;
        private Task SecureVaultLockAsync(string _) { VaultDatabase.Current.Lock(); return Task.CompletedTask; }
        private void AppendLog(string _) { }
    }
    public sealed class RunAdapter { public bool IsRunning => false; }
    public sealed class TeachAdapter { public bool IsBusy => false; }
    public sealed class HelloAdapter { public void InvalidateOutstandingEnables() { } }
    public sealed class StoreAdapter { public void SaveDurable(Configuration _, long __) => throw new Exception("Hello persistence is outside this adapter's scope."); }
}
namespace LaunchFlow.Services
{
    public static class WindowsHelloService
    {
        public static bool IsEnabled => false;
        public static void DisableStrict() => throw new Exception("Native Hello must not run.");
    }
}

internal static class MasterAdmissionTests
{
    internal static void Run(string root)
    {
        var current = VaultDatabase.Current;
        const string beforePassword = "é漢🙂Z7! Keep exact ";
        if (!current.Unlock(beforePassword)) throw new Exception("Master admission healthy control could not unlock.");
        var snapshot = UiTests.Snapshot(root);
        LaunchFlow.SecretPromptWindow.Submissions.Enqueue(beforePassword);
        LaunchFlow.SecretPromptWindow.Submissions.Enqueue("aZ7!aZ7!");
        new LaunchFlow.MainWindow().ChangeForTest();
        if (!UiTests.Equal(snapshot, UiTests.Snapshot(root)) || !current.IsUnlocked) throw new Exception("Actual master-password handler changed data for short input.");
        Console.WriteLine("PASS actual_master_change_short_rejection_preserves_complete_snapshot");
        const string next = "é漢🙂Z7! Independent exact ";
        LaunchFlow.SecretPromptWindow.Submissions.Enqueue(beforePassword);
        LaunchFlow.SecretPromptWindow.Submissions.Enqueue(next);
        LaunchFlow.SecretPromptWindow.Submissions.Enqueue(next);
        new LaunchFlow.MainWindow().ChangeForTest();
        if (current.IsUnlocked) throw new Exception("Actual master-password handler did not finish rotation/lock.");
        var cold = new VaultDatabase(current.DatabasePath);
        if (cold.Unlock(beforePassword) || !cold.Unlock(next)) throw new Exception("Actual master-password handler failed cold exact-input rotation.");
        cold.Lock();
        Console.WriteLine("PASS actual_master_change_eligible_unicode_cold_rotation");
    }
}
