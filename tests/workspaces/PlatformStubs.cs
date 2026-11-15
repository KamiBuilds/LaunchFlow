using System.Drawing;

// Monitor enumeration seam only; no claim of native Windows display verification.
namespace System.Windows.Forms;
public sealed class Screen
{
    public static Screen[] AllScreens { get; set; } = Array.Empty<Screen>();
    public string DeviceName { get; init; } = "";
    public bool Primary { get; init; }
    public Rectangle Bounds { get; init; }
    public System.Drawing.Rectangle WorkingArea => Bounds;
}
