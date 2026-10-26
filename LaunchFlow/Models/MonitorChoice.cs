using System.Drawing;

namespace LaunchFlow.Models;

public sealed class MonitorChoice
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public Rectangle Bounds { get; init; }

    public override string ToString() => Name;
}
