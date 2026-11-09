using System.Windows;
using LaunchFlow.Models;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class WorkspacePreviewWindow : Window
{
    private sealed record PreviewRow(string Name, string Monitor, string Browser, string Status);

    public WorkspacePreviewWindow(WorkspaceProfile profile, Configuration config)
    {
        InitializeComponent();
        Heading.Text = profile.Name;
        var monitors = MonitorService.GetMonitors();
        var installed = BrowserEnvironment.Probe();
        var policy = EnterprisePolicy.Current;
        WorkflowRows.ItemsSource = profile.Resolve(config).Select(workflow =>
        {
            var monitor = MonitorService.Resolve(workflow, monitors);
            var ready = !policy.DisableAutomation && policy.IsUrlAllowed(workflow.StartUrl);
            var available = workflow.Browser.Contains("Edge", StringComparison.OrdinalIgnoreCase)
                ? installed.Edge
                : workflow.Browser.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
                    ? installed.Chrome
                    : workflow.Browser.Contains("Default", StringComparison.OrdinalIgnoreCase);
            var status = !ready ? "Policy blocked" : available ? "Ready" : "Browser setup";
            return new PreviewRow(workflow.Name, monitor.Name, workflow.Browser, status);
        }).ToArray();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
