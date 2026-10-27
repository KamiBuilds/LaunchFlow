// Native screen enumeration only; recorder harness links the real monitor resolver.
namespace System.Windows.Forms;
public sealed class Screen
{
    public static Screen[] AllScreens { get; set; } = Array.Empty<Screen>();
    public string DeviceName { get; init; } = "";
    public bool Primary { get; init; }
    public System.Drawing.Rectangle Bounds { get; init; }
    public System.Drawing.Rectangle WorkingArea => Bounds;
}
