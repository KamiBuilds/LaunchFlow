using System.Collections.Concurrent;
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
    private readonly DispatcherTimer _drainTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ConcurrentQueue<CapturedAction> _pending = new();
    private readonly string _bindingName = "__launchFlowTeach_" + Guid.NewGuid().ToString("N");
    private IAsyncDisposable? _binding;
    private bool _capturing;
    private bool _stopping;

    public bool Saved { get; private set; }
    public List<WorkflowStep> RecordedSteps { get; } = new();

    public VisualRecorderWindow(IPage page)
    {
        _page = page;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            await StartRecordingAsync();
        };
        _drainTimer.Tick += (_, _) => DrainActions();
    }

    // Browser-to-app notifications keep clicks that would be lost on navigation.
    // AddInitScriptAsync reattaches the click listener to each navigated frame.
    private async Task StartRecordingAsync()
    {
        if (_stopping || _capturing) return;
        try
        {
            if (_binding is null)
            {
                _binding = await _page.ExposeBindingAsync<string>(_bindingName,
                    (_, json) =>
                    {
                        if (!_capturing || _stopping || json.Length > 8192) return;
                        try
                        {
                            var action = JsonSerializer.Deserialize<CapturedAction>(json,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (action?.Type == "Click" && !string.IsNullOrWhiteSpace(action.Target))
                                _pending.Enqueue(action);
                        }
                        catch (JsonException) { /* Ignore malformed messages. */ }
                    });
                await _page.AddInitScriptAsync(BuildRecorderScript());
            }
            await _page.EvaluateAsync(BuildRecorderScript());
            _capturing = true;
            _drainTimer.Start();
            CaptureToggle.Content = "Pause recording";
            CaptureStatus.Text = "Recording clicks… Switch to the browser and click something.";
        }
        catch (Exception ex)
        {
            _capturing = false;
            CaptureStatus.Text = "Could not start click recording. You can still add a step manually.";
            MessageBox.Show(this, ex.Message, "Teach couldn't start", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildRecorderScript() => $$"""
        () => {
          const binding = '{{_bindingName}}';
          if (window.__lfStopTeach) window.__lfStopTeach();
          let active = true;
          const cssPath = (element) => {
            if (!(element instanceof Element)) return '';
            if (element.id) return '#' + CSS.escape(element.id);
            const parts = [];
            let el = element;
            for (let i = 0; el && i < 7; i++, el = el.parentElement) {
              if (el.id) { parts.unshift('#' + CSS.escape(el.id)); break; }
              let name = el.tagName.toLowerCase();
              const parent = el.parentElement;
              if (parent) {
                const similar = [...parent.children].filter(n => n.tagName === el.tagName);
                if (similar.length > 1) name += ':nth-of-type(' + (similar.indexOf(el) + 1) + ')';
              }
              parts.unshift(name);
            }
            return parts.join(' > ');
          };
          const onClick = (event) => {
            if (!active || !event.isTrusted) return;
            const target = event.target;
            if (!(target instanceof Element)) return;
            // Never record typed values, passwords, or editable inputs.
            if (target.closest('input,textarea,[contenteditable],select,[type=password]')) return;
            const el = target.closest('a,button,[role="button"],[role="link"],label') || target;
            const selector = cssPath(el);
            const report = window[binding];
            if (!selector || typeof report !== 'function') return;
            const label = (el.getAttribute('aria-label') || el.textContent || '').trim().slice(0, 48);
            void report(JSON.stringify({type:'Click',target:selector,label})).catch(() => {});
          };
          document.addEventListener('click', onClick, true);
          window.__lfStopTeach = () => {
            active = false;
            document.removeEventListener('click', onClick, true);
          };
          return true;
        }
        """;

    private async void ToggleCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping) return;
        if (_capturing)
        {
            _capturing = false;
            DrainActions();
            CaptureToggle.Content = "Resume recording";
            CaptureStatus.Text = "Paused — " + RecordedSteps.Count + " click(s) captured.";
        }
        else
        {
            try { await _page.EvaluateAsync(BuildRecorderScript()); }
            catch { /* The init script will reattach after navigation. */ }
            _capturing = true;
            CaptureToggle.Content = "Pause recording";
            CaptureStatus.Text = "Recording clicks…";
        }
    }

    private void DrainActions()
    {
        bool changed = false;
        while (_pending.TryDequeue(out var item))
        {
            if (string.IsNullOrWhiteSpace(item.Target)) continue;
            var description = string.IsNullOrWhiteSpace(item.Label)
                ? "Click " + item.Target
                : "Click “" + item.Label + "”";
            RecordedSteps.Add(new WorkflowStep
            {
                Type = "Click", Target = item.Target, Description = description
            });
            changed = true;
        }
        if (changed)
        {
            RefreshList();
            CaptureStatus.Text = "Captured " + RecordedSteps.Count + " click(s). Click Save when finished.";
        }
    }

    private void CaptureUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = _page.Url;
            AutomationEngine.ValidateUrl(url);
            AddStep("Navigate", url, "Open " + url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Open a website in the browser first. " + ex.Message,
                "No page yet", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AddNavigate_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Navigate", Target = "https://", Description = "Open a page" });
    private void AddClick_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Click", Target = "button", Description = "Click" });
    private void AddWait_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Wait", Target = "1500", Description = "Wait" });

    private void EditNew(WorkflowStep step)
    {
        var editor = new StepEditorWindow(step) { Owner = this };
        if (editor.ShowDialog() == true)
            AddStep(editor.Result.Type, editor.Result.Target,
                editor.Result.Description ?? editor.Result.Type + " " + editor.Result.Target,
                editor.Result.Value);
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
        while (_pending.TryDequeue(out _)) { }
        RefreshList();
        CaptureStatus.Text = "No clicks saved yet. Try clicking something on the website.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping) return;
        SaveStepsButton.IsEnabled = false;
        await Task.Delay(200);
        DrainActions();
        if (RecordedSteps.Count == 0)
        {
            SaveStepsButton.IsEnabled = true;
            CaptureStatus.Text = "Nothing was recorded yet. Click a button on the website, then try Save clicks.";
            return;
        }
        Saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Saved = false;
        RecordedSteps.Clear();
        Close();
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _stopping = true;
        _capturing = false;
        _drainTimer.Stop();
        try { await _page.EvaluateAsync("() => { if (window.__lfStopTeach) window.__lfStopTeach(); }"); }
        catch { /* Browser may have closed. */ }
        if (_binding is not null)
        {
            try { await _binding.DisposeAsync(); }
            catch { /* Browser may have disposed the binding. */ }
            _binding = null;
        }
    }

    private void AddStep(string type, string target, string description, string? value = null)
    {
        RecordedSteps.Add(new WorkflowStep
        {
            Type = type, Target = target, Value = value ?? "", Description = description
        });
        RefreshList();
    }

    private void RefreshList()
    {
        StepList.ItemsSource = null;
        StepList.ItemsSource = RecordedSteps;
        if (RecordedSteps.Count > 0) StepList.ScrollIntoView(RecordedSteps[^1]);
    }

    private sealed class CapturedAction
    {
        public string? Type { get; set; }
        public string Target { get; set; } = "";
        public string? Label { get; set; }
    }
}
