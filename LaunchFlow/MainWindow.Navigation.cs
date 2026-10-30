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

/// <summary>Keyboard shortcuts, navigation state and authorized Settings selection.</summary>
public partial class MainWindow
{
    /// <summary>
    /// Keep the primary Run / Stop group centered at compact window widths.
    /// The Save button still indicates unsaved changes through its weight and
    /// tooltip, even when the longer status caption has to be hidden.
    /// </summary>
    private void ActionToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SaveStatusLabel is null) return; // Resize can fire during XAML construction.
        SaveStatusLabel.Visibility =
            e.NewSize.Width < 940 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool ParseShortcut(string shortcut, out uint flags, out uint key)
    {
        flags = 0x4000; // MOD_NOREPEAT
        key = 0;
        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        foreach (var part in parts[..^1])
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) flags |= 0x0002;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) flags |= 0x0001;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) flags |= 0x0004;
            else return false;
        }
        if ((flags & 7) == 0) return false;
        if (!Enum.TryParse<Key>(parts[^1], true, out var parsed)) return false;
        if (parsed is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return false;
        key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
        return key != 0;
    }

    private bool TrySetShortcut(string shortcut)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !ParseShortcut(shortcut, out uint modifiers, out uint key)) return false;
        if (_activeShortcut.Length > 0) UnregisterHotKey(handle, HotkeyId);
        if (!RegisterHotKey(handle, HotkeyId, modifiers, key))
        {
            if (_activeShortcut.Length > 0 && ParseShortcut(_activeShortcut, out var oldMods, out var oldKey))
                RegisterHotKey(handle, HotkeyId, oldMods, oldKey);
            AppendLog($"Shortcut {shortcut} is reserved or already in use by another app.");
            return false;
        }
        _activeShortcut = shortcut;
        ShortcutBox.Text = shortcut;
        ShortcutHelpLabel.Text = $"{shortcut} is active even while LaunchFlow is hidden in the tray.";
        AppendLog($"Global shortcut activated: {shortcut}.");
        return true;
    }

    private void CaptureShortcut_Click(object sender, RoutedEventArgs e)
    {
        _capturingShortcut = true;
        CaptureButton.Content = "Press keys…";
        ShortcutHelpLabel.Text = "Press Ctrl, Alt or Shift together with a letter or function key. Esc cancels.";
        Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingShortcut) return;
        if (e.Key == Key.Escape) { _capturingShortcut = false; CaptureButton.Content = "Change shortcut"; e.Handled = true; return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        var bits = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) bits.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) bits.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) bits.Add("Shift");
        if (bits.Count == 0) { ShortcutHelpLabel.Text = "A modifier (Ctrl, Alt or Shift) is required."; return; }
        bits.Add(key.ToString());
        var requested = string.Join("+", bits);
        _capturingShortcut = false;
        CaptureButton.Content = "Change shortcut";
        e.Handled = true;
        if (!TrySetShortcut(requested))
        {
            MessageBox.Show(this, $"The shortcut {requested} could not be registered. Try another.",
                "Shortcut unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _configuration.GlobalShortcut = requested;
        _store.Save(_configuration);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        SidebarPanel.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(_sidebarCollapsed ? 0 : 245);
        SidebarToggleButton.Content = _sidebarCollapsed ? "❯" : "❮";
    }

    // The navigation controls use the same MainTabs content and auth flow.
    private void TopNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, WorkflowsNav)) MainTabs.SelectedIndex = 0;
        else if (ReferenceEquals(sender, SettingsNav)) MainTabs.SelectedIndex = 1;
        SyncTopNavigation();
    }

    private void SyncTopNavigation()
    {
        WorkflowsNav.IsChecked = MainTabs.SelectedIndex == 0;
        SettingsNav.IsChecked = MainTabs.SelectedIndex == 1;
    }

    // The password editor is a secured Settings category, not a separate
    // top-level workspace. Authorization is required before showing it; leaving
    // the category clears pending secret inputs but retains the one-hour session.
    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configuration is null || SettingsTabs is null || MainTabs is null ||
            UsernameBox is null || PasswordInput is null || _revertingTab ||
            !ReferenceEquals(e.OriginalSource, SettingsTabs) || MainTabs.SelectedIndex != 1)
            return;
        UpdatePasswordSettingsAccess();
    }

    private void UpdatePasswordSettingsAccess()
    {
        if (SettingsTabs.SelectedIndex == 1)
        {
            if (!PasswordSessionIsValid() && !RequireMasterPassword())
            {
                _revertingTab = true;
                try { SettingsTabs.SelectedIndex = 0; }
                finally { _revertingTab = false; }
                ClearCredentialEditor();
                return;
            }
            if (_selected is not null) LoadCredentialEditor(_selected);
            PasswordSessionTimer_Tick(null, EventArgs.Empty);
        }
        else
        {
            // Never leave unsaved credential input values in inactive tabs.
            ClearCredentialEditor();
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configuration is null || MainTabs is null || SettingsTabs is null ||
            WorkflowsNav is null || SettingsNav is null ||
            UsernameBox is null || PasswordInput is null) return;
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        if (_revertingTab) { SyncTopNavigation(); return; }
        // Preserve pending website edits before navigating away, including
        // navigation into Settings. Password inputs are still explicit-save only.
        try { ApplyCurrentEditor(); }
        catch (Exception ex) { AppendLog("Could not preserve website edits during navigation: " + ex.GetType().Name); }
        if (MainTabs.SelectedIndex == 1)
            UpdatePasswordSettingsAccess();
        else
            ClearCredentialEditor();
        SyncTopNavigation();
    }

}
