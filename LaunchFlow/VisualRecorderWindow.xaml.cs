using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class VisualRecorderWindow : Window
{
    private readonly IPage _page;
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private bool _capturing;

    public List<WorkflowStep> RecordedSteps { get; } = new();

    public VisualRecorderWindow(IPage page)
    {
        _page = page;
        InitializeComponent();
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
        RefreshList();
        _pollTimer.Tick += PollTimer_Tick;
    }

    private async void PollTimer_Tick(object? sender, EventArgs e) => await PollClicksAsync();

    private async void ToggleCapture_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_capturing)
            {
                await InstallRecorderScriptAsync();
                _capturing = true;
                _pollTimer.Start();
                CaptureToggle.Content = "Stop click capture";
                CaptureStatus.Text = "Listening for clicks in the browser… Avoid typing passwords while capturing.";
                StatusText.Text = "Click capture is on. Interact with the site; steps appear in the list.";
            }
            else
            {
                await StopCaptureAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task InstallRecorderScriptAsync()
    {
        const string script = """
            () => {
              if (window.__lfRecorderInstalled) return true;
              window.__lfRecorderInstalled = true;
              window.__lfClicks = [];
              const esc = (s) => String(s).replace(/([ !"#$%&'()*+,./:;<=>?@[\\\]^`{|}~])/g, '\\$1');
              const cssPath = (el) => {
                if (!(el instanceof Element)) return '';
                if (el.id) return '#' + esc(el.id);
                const parts = [];
                let cur = el;
                for (let depth = 0; cur && cur.nodeType === 1 && depth < 6; depth++) {
                  let part = cur.nodeName.toLowerCase();
                  const parent = cur.parentElement;
                  if (cur.id) { parts.unshift('#' + esc(cur.id)); break; }
                  if (parent) {
                    const siblings = Array.from(parent.children).filter(c => c.nodeName === cur.nodeName);
                    if (siblings.length > 1) part += ':nth-of-type(' + (siblings.indexOf(cur) + 1) + ')';
                  }
                  parts.unshift(part);
                  cur = parent;
                }
                return parts.join(' > ');
              };
              const push = (type, target, extra) => {
                if (!target) return;
                window.__lfClicks.push({ type, target, value: extra || '' });
              };
              document.addEventListener('click', (e) => {
                const el = e.target;
                if (!(el instanceof Element)) return;
                if (el.closest('input[type=password], [type=password]')) return;
                const label = (el.innerText || el.getAttribute('aria-label') || '').trim().slice(0, 48);
                push('Click', cssPath(el), label);
              }, true);
              document.addEventListener('change', (e) => {
                const el = e.target;
                if (!(el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement)) return;
                if (el instanceof HTMLInputElement && el.type === 'password') return;
                push('Fill', cssPath(el), el.value || '');
              }, true);
              return true;
            }
            """;
        await _page.EvaluateAsync(script);
    }

    private async Task PollClicksAsync()
    {
        if (!_capturing) return;
        try
        {
            var json = await _page.EvaluateAsync<string>("""
                () => {
                  const items = window.__lfClicks || [];
                  window.__lfClicks = [];
                  return JSON.stringify(items);
                }
                """);
            if (string.IsNullOrWhiteSpace(json) || json == "[]") return;
            var batch = JsonSerializer.Deserialize<List<CapturedAction>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (batch is null || batch.Count == 0) return;
            foreach (var item in batch)
            {
                if (string.IsNullOrWhiteSpace(item.Target)) continue;
                var description = item.Type == "Fill"
                    ? $"Fill → {item.Target}"
                    : string.IsNullOrWhiteSpace(item.Value)
                        ? $"{item.Type} → {item.Target}"
                        : $"{item.Type} “{item.Value}” → {item.Target}";
                AddStep(item.Type ?? "Click", item.Target, description,
                    string.Equals(item.Type, "Fill", StringComparison.OrdinalIgnoreCase) ? item.Value : null);
            }
            CaptureStatus.Text = $"Captured {RecordedSteps.Count} step(s).";
        }
        catch
        {
            // Navigations briefly break evaluate; keep listening.
        }
    }

    private async Task StopCaptureAsync()
    {
        _capturing = false;
        _pollTimer.Stop();
        CaptureToggle.Content = "Start click capture";
        CaptureStatus.Text = "Click capture is off.";
        try
        {
            await _page.EvaluateAsync("() => { window.__lfRecorderInstalled = false; window.__lfClicks = []; }");
        }
        catch { /* page closed */ }
    }

    private async void CaptureUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = _page.Url;
            if (string.IsNullOrWhiteSpace(url) || url is "about:blank")
            {
                MessageBox.Show(this, "The page has no navigable URL yet.",
                    "Recorder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AutomationEngine.ValidateUrl(url);
            AddStep("Navigate", url, $"Navigate → {url}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await Task.CompletedTask;
    }

    private void AddNavigate_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Navigate", Target = "https://", Description = "Navigate" });

    private void AddClick_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Click", Target = "button", Description = "Click" });

    private void AddFill_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Fill", Target = "input", Value = "", Description = "Fill" });

    private void AddWait_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Wait", Target = "1500", Description = "Wait 1500 ms" });

    private void EditNew(WorkflowStep seed)
    {
        var editor = new StepEditorWindow(seed) { Owner = this };
        if (editor.ShowDialog() == true)
            AddStep(editor.Result.Type, editor.Result.Target, Describe(editor.Result), editor.Result.Value);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem is WorkflowStep step)
        {
            RecordedSteps.Remove(step);
            RefreshList();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        RecordedSteps.Clear();
        RefreshList();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await StopCaptureAsync();
        DialogResult = true;
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        await StopCaptureAsync();
        RecordedSteps.Clear();
        DialogResult = false;
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _pollTimer.Tick -= PollTimer_Tick;
        await StopCaptureAsync();
    }

    private void AddStep(string type, string target, string description, string? value = null)
    {
        RecordedSteps.Add(new WorkflowStep
        {
            Type = type,
            Target = target,
            Value = value ?? "",
            Description = description
        });
        RefreshList();
    }

    private static string Describe(WorkflowStep s) =>
        string.IsNullOrWhiteSpace(s.Description) ? $"{s.Type} → {s.Target}" : s.Description;

    private void RefreshList()
    {
        foreach (var step in RecordedSteps)
        {
            if (string.IsNullOrWhiteSpace(step.Description))
                step.Description = $"{step.Type} → {step.Target}";
        }
        StepList.ItemsSource = null;
        StepList.ItemsSource = RecordedSteps;
    }

    private sealed class CapturedAction
    {
        public string? Type { get; set; }
        public string Target { get; set; } = "";
        public string? Value { get; set; }
    }
}
