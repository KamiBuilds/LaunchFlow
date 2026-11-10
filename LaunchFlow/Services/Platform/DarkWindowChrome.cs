using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LaunchFlow.Services;

/// <summary>
/// Enables Windows 10/11 immersive dark title bars so the light system
/// caption bar does not clash with LaunchFlow's dark UI.
/// </summary>
public static class DarkWindowChrome
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void Apply(Window window)
    {
        if (!window.IsLoaded)
        {
            window.Loaded += (_, _) => Apply(window);
            return;
        }

        var helper = new WindowInteropHelper(window);
        var hwnd = helper.EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        int dark = 1;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));

        // Caption / border colors (Windows 11): 0x00BBGGRR
        int caption = 0x001B140F; // ~#0F141B
        int border = 0x00D7E76E;  // ~#6EE7D7 accent-ish
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));

        // Match WPF background so resize grip area blends.
        if (window.Background is not SolidColorBrush)
            window.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x12, 0x20));
    }
}
