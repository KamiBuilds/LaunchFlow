using LaunchFlow.Models;
using Forms = System.Windows.Forms;

namespace LaunchFlow.Services;

public static class MonitorService
{
    /// <summary>Prefer the saved Windows device name; a disconnected named display uses primary,
    /// not the unrelated display now occupying its old index. Does not mutate the saved identity.</summary>
    public static MonitorChoice Resolve(WebsiteWorkflow workflow, IReadOnlyList<MonitorChoice> monitors)
    {
        if (!string.IsNullOrWhiteSpace(workflow.MonitorDeviceName))
        {
            var saved = monitors.FirstOrDefault(monitor => string.Equals(
                monitor.DeviceName, workflow.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (saved is not null) return saved;
        }
        else
        {
            var legacy = monitors.FirstOrDefault(monitor => monitor.Index == workflow.MonitorIndex);
            if (legacy is not null) return legacy;
        }
        return monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors.FirstOrDefault() ??
            new MonitorChoice { Index = 0, Name = "Primary monitor", IsPrimary = true,
                Bounds = new System.Drawing.Rectangle(0, 0, 1920, 1080) };
    }

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
                IsPrimary = screen.Primary,
                Bounds = screen.Bounds
            });
        }

        if (list.Count == 0)
        {
            list.Add(new MonitorChoice
            {
                Index = 0,
                Name = "Primary monitor",
                IsPrimary = true,
                Bounds = new System.Drawing.Rectangle(0, 0, 1920, 1080)
            });
        }

        return list;
    }
}
