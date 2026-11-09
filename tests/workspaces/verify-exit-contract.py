#!/usr/bin/env python3
"""Source-linked exit contract; this cannot substitute for native Windows acceptance."""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
src = (root / "LaunchFlow/UI/Shell/MainWindow.xaml.cs").read_text(encoding="utf-8")
module = (root / "LaunchFlow/UI/Shell/MainWindow.Lifecycle.cs").read_text(encoding="utf-8")
closing = src.split("private void OnClosing(", 1)[1].split("private IntPtr HotkeyHook(", 1)[0]
closed = src.split("private void OnClosed(", 1)[1]
tray = src.split("private void ExitFromTray(", 1)[1].split("internal void RequestCrashCleanup(", 1)[0]
installer = src.split("internal async Task RequestShutdownForInstallAsync(", 1)[1].split("private static void AppendLog(", 1)[0]
checks = {
    "WPF Closing requires an owned close permit": "if (_exitDrain.Permitted)" in closing and "if (_exiting)" in closing and "e.Cancel = true;" in closing,
    "ordinary close schedules an owned async exit after cancellation": "Dispatcher.BeginInvoke(new Action(() => _ = CompleteOwnedExitAsync(installer: false)))" in closing,
    "tray never closes directly": "CompleteOwnedExitAsync(installer: false)" in tray and "Close();" not in tray,
    "installer uses same completion operation": "await CompleteOwnedExitAsync(installer: true)" in installer,
    "OnClosed no longer owns normal asynchronous cleanup": "private void OnClosed(" in src and "private async void OnClosed(" not in src,
    "browser close/lock finishes before disposal and WPF Close": module.index("await SecureVaultLockAsync(") < module.index("await _runner.DisposeAsync();") < module.index("Close();"),
    "failed drain revokes permit and re-enables UI": "_exitDrain.RevokePermit();" in module and "IsEnabled = true;" in module,
    "UI disabled while exit is pending": "IsEnabled = false;" in module,
    "installer error catch avoids modal dialog": "MessageBox.Show(" not in installer and "ShowFromTray();" not in installer,
}
for name, passed in checks.items():
    if not passed:
        raise SystemExit("FAIL " + name)
    print("PASS " + name)
print("Production-source checks PASS; native WPF/OS, updater and cross-session acceptance unexecuted.")
