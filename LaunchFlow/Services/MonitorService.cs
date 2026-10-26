using LaunchFlow.Models;
using Forms = System.Windows.Forms;

namespace LaunchFlow.Services;

public static class MonitorService
{
    public static IReadOnlyList<MonitorChoice> GetMonitors()
    {
        var screens = Forms.Screen.AllScreens;
        var list = new List<MonitorChoice>(screens.Length);
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var label = screen.Primary
                ? $"Monitor {i + 1} (Primary) — {screen.Bounds.Width}×{screen.Bounds.Height}"
                : $"Monitor {i + 1} — {screen.Bounds.Width}×{screen.Bounds.Height}";
            list.Add(new MonitorChoice
            {
                Index = i,
                Name = label,
                DeviceName = screen.DeviceName,
                Bounds = screen.Bounds
            });
        }

        if (list.Count == 0)
        {
            list.Add(new MonitorChoice
            {
                Index = 0,
                Name = "Primary monitor",
                Bounds = new System.Drawing.Rectangle(0, 0, 1920, 1080)
            });
        }

        return list;
    }
}
