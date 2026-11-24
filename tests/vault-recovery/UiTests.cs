using LaunchFlow;
using LaunchFlow.Services;
using System.Reflection;
using System.Windows;

internal static class UiTests
{
    private static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Click(object target, string method) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, [target, new RoutedEventArgs()]);
    internal static Dictionary<string, byte[]> Snapshot(string root)
    {
        var result = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllBytes);
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            result.Add(Path.GetRelativePath(root, directory) + "/", []);
        return result;
    }
    internal static bool Equal(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var bytes) && bytes.SequenceEqual(p.Value));
    internal static void Run(string root, string ownedPath)
    {
        var method = typeof(App).GetMethod("TryStartFreshVault", BindingFlags.Static | BindingFlags.NonPublic);
        Require(method is not null, "Actual App preserved-fresh-start handler missing.");
        bool Invoke(VaultDatabase db)
        {
            try { return (bool)method!.Invoke(null, [db])!; }
            catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
        foreach (var mode in new[] { "cancel", "close", "incomplete", "confirm" })
        {
            var folder = Path.Combine(root, "ui-" + mode); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "launchflow.db"); File.WriteAllText(path, "synthetic unknown");
            File.WriteAllText(path + "-wal", "synthetic companion wal"); File.WriteAllText(path + "-shm", "synthetic companion shm");
            var before = Snapshot(folder);
            Window.OnShow = window =>
            {
                var confirmation = (VaultFreshStartWindow)window;
                if (mode == "close") confirmation.Close();
                else if (mode == "cancel") Click(confirmation, "Cancel_Click");
                else
                {
                    confirmation.ConfirmationText.Text = "START NEW VAULT";
                    confirmation.PreserveCheck.IsChecked = mode == "confirm";
                    Click(confirmation, "Confirm_Click");
                }
                return confirmation.DialogResult;
            };
            var success = Invoke(new VaultDatabase(path));
            Require(success == (mode == "confirm"), "Fresh-start confirmation contract failed.");
            if (mode != "confirm") Require(Equal(before, Snapshot(folder)), "Decline/close/incomplete consent changed the complete file snapshot.");
            else
            {
                var preserved = Snapshot(Path.Combine(folder, "recovery"));
                Require(!File.Exists(path) && before.Values.All(bytes => preserved.Values.Any(copy => copy.SequenceEqual(bytes))) && preserved.Count == before.Count,
                    "Confirmed App recovery failed to preserve the complete set.");
            }
        }
        Console.WriteLine("PASS actual_fresh_confirmation_cancel_close_full_snapshot_and_confirmed_preservation");
        var shown = false; Window.OnShow = _ => { shown = true; return true; };
        Require(!Invoke(new VaultDatabase(ownedPath)) && !shown, "Owned vault reached fresh-start confirmation.");
        Console.WriteLine("PASS actual_app_owned_vault_never_offers_fresh_start");
        var raceFolder = Path.Combine(root, "ui-dialog-race"); Directory.CreateDirectory(raceFolder);
        var race = Path.Combine(raceFolder, "launchflow.db"); File.WriteAllText(race, "synthetic unknown");
        Window.OnShow = window =>
        {
            File.Copy(ownedPath, race, overwrite: true);
            var confirmation = (VaultFreshStartWindow)window; confirmation.ConfirmationText.Text = "START NEW VAULT"; confirmation.PreserveCheck.IsChecked = true;
            Click(confirmation, "Confirm_Click"); return confirmation.DialogResult;
        };
        Exception? refusal = null; try { Invoke(new VaultDatabase(race)); } catch (Exception e) { refusal = e; }
        Require(refusal is not null && File.ReadAllBytes(race).SequenceEqual(File.ReadAllBytes(ownedPath)), "Dialog reentrancy bypassed owned-vault protection.");
        Console.WriteLine("PASS actual_app_rechecks_reentrant_owned_replacement_before_quarantine");
        foreach (var mode in new[] { "cancel", "close", "fresh-decline", "fresh-confirm" })
        {
            var current = VaultDatabase.Current;
            File.WriteAllText(current.DatabasePath, "synthetic caller unknown");
            File.WriteAllText(current.DatabasePath + "-wal", "synthetic caller wal");
            File.WriteAllText(current.DatabasePath + "-shm", "synthetic caller shm");
            var before = Snapshot(root);
            Window.OnShow = window =>
            {
                if (window is VaultRecoveryWindow recovery)
                {
                    if (mode == "cancel") Click(recovery, "Cancel_Click");
                    else if (mode == "close") recovery.Close();
                    else Click(recovery, "Fresh_Click");
                    return recovery.DialogResult;
                }
                var confirmation = (VaultFreshStartWindow)window;
                if (mode == "fresh-decline") Click(confirmation, "Cancel_Click");
                else
                {
                    confirmation.ConfirmationText.Text = "START NEW VAULT"; confirmation.PreserveCheck.IsChecked = true;
                    Click(confirmation, "Confirm_Click");
                }
                return confirmation.DialogResult;
            };
            var success = App.RecoverForTest();
            Require(success == (mode == "fresh-confirm"), "Actual recovery dispatcher did not honor deliberate choice.");
            if (!success) Require(Equal(before, Snapshot(root)), "Actual recovery dispatcher cancellation changed the full snapshot.");
            else Require(!File.Exists(current.DatabasePath) && Directory.GetFiles(Path.Combine(root, "recovery"), "*.db").Any(p => File.ReadAllText(p) == "synthetic caller unknown"), "Actual recovery dispatcher did not preserve the original.");
        }
        Console.WriteLine("PASS actual_recovery_dispatcher_choice_cancel_close_and_preserved_fresh_start");
        var backup = Path.Combine(root, "synthetic.launchflow");
        PortableExport.Write(backup, "Synthetic!Export9", new PortableExport.PortablePackage { Config = new(), Credentials = [] });
        foreach (var mode in new[] { "short", "mismatch", "valid" })
        {
            var current = VaultDatabase.Current; current.Lock();
            File.WriteAllText(current.DatabasePath, "synthetic portable original");
            var before = Snapshot(root);
            Microsoft.Win32.OpenFileDialog.Paths.Enqueue(backup);
            SecretPromptWindow.Submissions.Enqueue("Synthetic!Export9");
            var password = mode == "short" ? "aZ7!aZ7!" : "é漢🙂Z7! Keep exact ";
            SecretPromptWindow.Submissions.Enqueue(password);
            if (mode != "short") SecretPromptWindow.Submissions.Enqueue(mode == "mismatch" ? "Synthetic!Other9" : password);
            Window.OnShow = window => { var recovery = (VaultRecoveryWindow)window; Click(recovery, "Import_Click"); return recovery.DialogResult; };
            var success = App.RecoverForTest();
            Require(success == (mode == "valid"), "Actual portable recovery admission disagreed.");
            if (!success) Require(Equal(before, Snapshot(root)), "Rejected portable recovery changed the complete file snapshot.");
            else
            {
                Require(current.IsUnlocked, "Portable recovery reported success without a live verified vault.");
                current.Lock(); var cold = new VaultDatabase(current.DatabasePath);
                Require(cold.Unlock(password), "Portable recovery did not retain the exact independently verifiable input."); cold.Lock();
            }
        }
        Console.WriteLine("PASS actual_portable_recovery_short_mismatch_preserve_and_valid_cold_verification");
        Window.OnShow = null;
    }
}
