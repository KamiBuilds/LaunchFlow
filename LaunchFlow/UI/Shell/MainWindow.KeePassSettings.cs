using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Win32;
using Microsoft.Playwright;
using Forms = System.Windows.Forms;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace LaunchFlow;

/// <summary>KeePassXC database discovery and settings UI; credential prompts remain guarded.</summary>
public partial class MainWindow
{
    private async void ChooseKeePassEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is null) return;
        // A new chooser supersedes an older pending chooser independently of
        // workflow/editor edits. Only the current operation may publish cache.
        var chooserGeneration = checked(++_keepassChooserGeneration);
        var cacheRevision = _keepassCacheRevision;
        var ownedConfig = _configuration;
        var ownedWorkflow = _editorSession.Selection;
        var editorOwner = _editorSession.CaptureOwnership();
        var ownedVaultSession = VaultDatabase.Current.SessionGeneration;
        var keepassEntryVisible = KeepassEntryBox.Text;
        string? ownedDatabase = null;
        string? ownedKeyFile = null;

        bool StillOwns() =>
            _keepassChooserGeneration == chooserGeneration &&
            _keepassCacheRevision == cacheRevision &&
            ReferenceEquals(_configuration, ownedConfig) &&
            ownedConfig.Workflows.Any(w => ReferenceEquals(w, ownedWorkflow)) &&
            _editorSession.IsCurrent(editorOwner) &&
            ReferenceEquals(_editorSession.Selection, ownedWorkflow) &&
            !_vaultLockInProgress && !_windowsLocked && !_exiting &&
            VaultDatabase.Current.IsUnlocked &&
            VaultDatabase.Current.SessionGeneration == ownedVaultSession &&
            PasswordSessionIsValid() &&
            CredentialReadAllowed() &&
            (ownedDatabase is null || string.Equals(
                KeePassDbBox.Text.Trim(), ownedDatabase, StringComparison.Ordinal)) &&
            (ownedKeyFile is null || string.Equals(
                KeePassKeyBox.Text.Trim(), ownedKeyFile, StringComparison.Ordinal));

        // A stale continuation must not clear a newer cache or update its status.
        // The active lifecycle owner handles clearing on lock/policy/DB changes.
        bool Authorized()
        {
            if (StillOwns()) return true;
            // The CURRENT operation may revoke the cache when policy denies
            // reading it. A newer picker/run/cache revision is never touched.
            if (_keepassChooserGeneration == chooserGeneration &&
                _keepassCacheRevision == cacheRevision &&
                ReferenceEquals(_configuration, ownedConfig) &&
                _editorSession.IsCurrent(editorOwner) &&
                VaultDatabase.Current.SessionGeneration == ownedVaultSession &&
                !CredentialReadAllowed())
            {
                PublishKeePassCache(null);
                cacheRevision = _keepassCacheRevision;
            }
            return false;
        }
        if (!Authorized()) return;
        if (!KeePassXcService.IsCliAvailable())
        {
            MessageBox.Show(this, "KeePassXC could not be found. Install it and try again.",
                "KeePassXC unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var database = KeePassDbBox.Text.Trim();
        if (!File.Exists(database))
        {
            var found = KeePassXcService.FindDatabases();
            if (found.Count == 1)
            {
                if (!Authorized()) return;
                // Discovery is an intentional synchronous settings edit. Suppress
                // only its own TextChanged revision, then explicitly schedule
                // normal autosave and capture that one known new revision before
                // any await or user prompt can intervene.
                var wasLoading = _loadingEditor;
                _loadingEditor = true;
                try { SetKeePassDatabase(found[0].Path); }
                finally { _loadingEditor = wasLoading; }
                cacheRevision = _keepassCacheRevision; // owned synchronous cache reset
                if (!Authorized()) return;
                MarkUnsavedChanges();
                editorOwner = _editorSession.CaptureOwnership();
                database = found[0].Path;
            }
            else
            {
                SettingsTabs.SelectedIndex = 3;
                MainTabs.SelectedIndex = 1;
                MessageBox.Show(this,
                    "First choose your KeePassXC database in Settings → Security. Then open Settings → Passwords and click Choose KeePassXC login.",
                    "Connect KeePassXC", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        if (!Authorized()) return;
        // Freeze database/key inputs for this operation; do not re-read after awaits.
        var keyFile = KeePassKeyBox.Text.Trim();
        ownedDatabase = database;
        ownedKeyFile = keyFile;

        var secret = _keepassMasterPassword;
        if (secret is null)
        {
            var prompt = new SecretPromptWindow("Unlock KeePassXC",
                "Enter the KeePassXC master password to list your login names. Your saved passwords will not be displayed or copied into LaunchFlow.")
            { Owner = this };
            if (prompt.ShowDialog() != true) return;
            if (!Authorized()) return;
            secret = prompt.Secret ?? "";
        }

        try
        {
            if (!Authorized()) return;
            var entries = await KeePassXcService.ListEntriesAsync(
                ownedDatabase, ownedKeyFile, secret);
            if (!Authorized()) return;
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "No login entries were found in this KeePassXC database.",
                    "No logins found", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var picker = new KeePassEntryPickerWindow(entries) { Owner = this };
            if (picker.ShowDialog() != true || string.IsNullOrEmpty(picker.SelectedPath) || !Authorized()) return;
            var selectedPath = picker.SelectedPath;
            bool hasLocalCredential = _vault.Has(ownedWorkflow.Id);
            var result = _saveTransaction.Commit(
                ownedConfig,
                draft =>
                {
                    var target = draft.Workflows.FirstOrDefault(w => w.Id == ownedWorkflow.Id);
                    if (target is null)
                        throw new InvalidOperationException("KeePass target workflow missing from draft.");
                    target.KeepassEntryPath = selectedPath;
                    draft.KeePassDatabasePath = ownedDatabase;
                    draft.KeePassKeyFilePath = ownedKeyFile;
                },
                StillOwns,
                ownedVaultSession,
                published =>
                {
                    // Publish validated in-place model assignments only. Do not
                    // pump UI or trigger TextChanged inside the storage transaction.
                    ownedWorkflow.KeepassEntryPath = selectedPath;
                    ownedConfig.KeePassDatabasePath = ownedDatabase;
                    ownedConfig.KeePassKeyFilePath = ownedKeyFile;
                });
            if (!result.Published)
            {
                // A rejection/supersession cannot clear a cached password that
                // may belong to a newer picker. A committed-but-superseded save
                // is explicitly not a rollback.
                AppendLog("KeePass selection was not published (" + result.State + ").");
                return;
            }
            if (!StillOwns()) return;
            if (string.Equals(KeepassEntryBox.Text, keepassEntryVisible, StringComparison.Ordinal))
            {
                var wasLoading = _loadingEditor;
                _loadingEditor = true;
                try { KeepassEntryBox.Text = selectedPath; }
                finally { _loadingEditor = wasLoading; }
            }
            if (!StillOwns()) return;
            PublishKeePassCache(secret);
            cacheRevision = _keepassCacheRevision; // current operation's publication
            CredentialStatus.Text = DescribeCredentialStatus(ownedWorkflow);
            if (hasLocalCredential && StillOwns())
                MessageBox.Show(this,
                    "Login selected. This website already has a password saved in LaunchFlow, which takes priority. To use KeePassXC, click Remove saved credentials in Settings → Passwords.",
                    "KeePassXC login selected", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            // An older operation may fail after a newer operation published
            // its cache/status. Never mutate or warn in that newer owner.
            if (!StillOwns()) return;
            AppendLog("KeePass selection failed: " + ex.GetType().Name);
            MessageBox.Show(this, "The KeePassXC login could not be selected. Check vault authorization, organization policy and the database connection.", "Couldn't read KeePassXC",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshKeePassStatus()
    {
        var database = KeePassDbBox.Text.Trim();
        if (!KeePassXcService.IsCliAvailable())
        {
            KeePassStatusLabel.Text = "KeePassXC was not found. Install KeePassXC, then click Find my database.";
            return;
        }
        if (string.IsNullOrWhiteSpace(database))
        {
            KeePassStatusLabel.Text = "KeePassXC found. Choose a password database to continue.";
            return;
        }
        if (!File.Exists(database))
        {
            KeePassStatusLabel.Text = "The selected database is missing. Choose it again.";
            return;
        }
        KeePassStatusLabel.Text = $"KeePassXC found · {Path.GetFileName(database)} selected.";
    }

    private void SuggestKeePassDatabase()
    {
        if (!string.IsNullOrWhiteSpace(KeePassDbBox.Text)) return;
        var found = KeePassXcService.FindDatabases();
        if (found.Count == 1) KeePassDbBox.Text = found[0].Path;
    }

    private void FindKeePassDb_Click(object sender, RoutedEventArgs e)
    {
        var found = KeePassXcService.FindDatabases();
        if (found.Count == 0)
        {
            KeePassDbSuggestions.Visibility = Visibility.Collapsed;
            MessageBox.Show(this,
                "No KeePassXC database was found in the usual folders. Click Choose a file and select the .kdbx database you open in KeePassXC.",
                "Find KeePassXC database", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (found.Count == 1)
        {
            SetKeePassDatabase(found[0].Path);
            KeePassDbSuggestions.Visibility = Visibility.Collapsed;
            return;
        }

        KeePassDbSuggestions.ItemsSource = found;
        KeePassDbSuggestions.SelectedIndex = -1;
        KeePassDbSuggestions.Visibility = Visibility.Visible;
        KeePassStatusLabel.Text = $"Found {found.Count} databases. Choose the one you normally use.";
        KeePassDbSuggestions.Focus();
        KeePassDbSuggestions.IsDropDownOpen = true;
    }

    private void KeePassDbSuggestion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configuration is null || KeePassDbSuggestions is null || KeePassDbBox is null) return;
        if (KeePassDbSuggestions.SelectedItem is KeePassXcService.DatabaseChoice selected)
        {
            SetKeePassDatabase(selected.Path);
            KeePassDbSuggestions.Visibility = Visibility.Collapsed;
        }
    }

    // Every transient cache publication/revocation carries an independent epoch.
    // Older chooser continuations cannot adopt a newer run's cache by matching
    // a password value, workflow GUID or the ambient unlocked vault session.
    private void PublishKeePassCache(string? secret)
    {
        _keepassMasterPassword = secret;
        checked { _keepassCacheRevision++; }
    }

    private void SetKeePassDatabase(string path)
    {
        KeePassDbBox.Text = path;
        PublishKeePassCache(null);
        RefreshKeePassStatus();
    }

    private void UpdateKeePassEntryLabel()
    {
        var path = KeepassEntryBox.Text.Trim();
        SelectedKeePassEntryLabel.Text = string.IsNullOrWhiteSpace(path)
            ? "No KeePassXC login selected" : path;
    }

    private void KeePassEntryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (SelectedKeePassEntryLabel is not null) UpdateKeePassEntryLabel();
    }


    private void BrowseKeePassDb_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select KeePassXC database",
            Filter = "KeePass database (*.kdbx)|*.kdbx|All files (*.*)|*.*"
        };
        if (!string.IsNullOrWhiteSpace(KeePassDbBox.Text))
        {
            try
            {
                var dir = Path.GetDirectoryName(KeePassDbBox.Text.Trim());
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    dialog.InitialDirectory = dir;
            }
            catch { /* ignore bad path */ }
        }
        if (dialog.ShowDialog(this) == true)
        {
            SetKeePassDatabase(dialog.FileName);
        }
    }

    private void BrowseKeePassKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select KeePass key file",
            Filter = "Key file (*.keyx;*.key)|*.keyx;*.key|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            KeePassKeyBox.Text = dialog.FileName;
            PublishKeePassCache(null);
            RefreshKeePassStatus();
        }
    }

}
