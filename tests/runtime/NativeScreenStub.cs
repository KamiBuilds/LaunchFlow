// Native display enumeration seam; resolution and CDP command construction remain production code.
namespace System.Windows.Forms;
public sealed class Screen
{
    public static Screen[] AllScreens { get; set; } = Array.Empty<Screen>();
    public string DeviceName { get; init; } = "";
    public bool Primary { get; init; }
    public System.Drawing.Rectangle Bounds { get; init; }
    public System.Drawing.Rectangle WorkingArea => Bounds;
}
