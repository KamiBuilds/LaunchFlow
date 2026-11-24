using System.Windows;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class App
{
    internal static bool TryStartFreshVault(VaultDatabase db)
    {
        // Inspect an isolated snapshot so declining or closing confirmation cannot create sidecars.
        if (db.IsUnlocked || !VaultRecoveryService.CanStartFreshVault(db.DatabasePath)) return false;
        var confirmation = new VaultFreshStartWindow();
        if (confirmation.ShowDialog() != true) return false;
        StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultRecoveryStarted);
        db.Lock();
        // Revalidates before and after moving the set; a modal replacement grants no reset authority.
        VaultRecoveryService.QuarantineLiveVault(db.DatabasePath, db.DataDirectory);
        MessageBox.Show("Existing files were preserved under the vault recovery folder. " +
            "Set up a new empty vault next. Keep the preserved database and its companions together; " +
            "the new password does not recover those files.",
            "Files preserved", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }
}
