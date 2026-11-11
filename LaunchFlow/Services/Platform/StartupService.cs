using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LaunchFlow.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LaunchFlow";

    public static void Apply(bool startWithWindows)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
                        ?? throw new InvalidOperationException("Cannot open HKCU Run key.");

        if (!startWithWindows)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exe = Environment.ProcessPath
                   ?? Process.GetCurrentProcess().MainModule?.FileName
                   ?? throw new InvalidOperationException("Cannot resolve LaunchFlow executable path.");
        key.SetValue(ValueName, $"\"{exe}\" --tray");
    }
}
