using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class VaultRecoveryWindow : Window
{
    internal enum RecoveryChoice { Cancel, RestoreSqlite, ImportPortable, StartFresh }
    internal RecoveryChoice Choice { get; private set; }

    public VaultRecoveryWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
        Closed += (_, _) => { if (DialogResult != true) Choice = RecoveryChoice.Cancel; };
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => Choose(RecoveryChoice.RestoreSqlite);
    private void Import_Click(object sender, RoutedEventArgs e) => Choose(RecoveryChoice.ImportPortable);
    private void Fresh_Click(object sender, RoutedEventArgs e) => Choose(RecoveryChoice.StartFresh);
    private void Cancel_Click(object sender, RoutedEventArgs e) { Choice = RecoveryChoice.Cancel; DialogResult = false; }
    private void Choose(RecoveryChoice choice) { Choice = choice; DialogResult = true; }
}
