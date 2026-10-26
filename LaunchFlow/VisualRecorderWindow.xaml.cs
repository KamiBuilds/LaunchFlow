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
    private readonly IBrowserContext _context;
    private readonly DispatcherTimer _drainTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly ConcurrentQueue<CapturedAction> _pending = new();
    private readonly string _bindingName = "__launchFlowTeach_" + Guid.NewGuid().ToString("N");
    private readonly string _stateName = "__launchFlowTeachState_" + Guid.NewGuid().ToString("N");
    private IAsyncDisposable? _binding;
    private bool _capturing;
    private bool _stopping;
    private bool _initializing;
    private bool _saving;

    public bool Saved { get; private set; }
    public List<WorkflowStep> RecordedSteps { get; } = new();

    public VisualRecorderWindow(IPage page)
    {
        _page = page;
        _context = page.Context;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            await StartRecordingAsync();
        };
        _drainTimer.Tick += (_, _) => DrainActions();
    }

    private async Task StartRecordingAsync()
    {
        if (_stopping || _initializing || _binding is not null) return;
        _initializing = true;
        _capturing = true;
        _drainTimer.Start();
        try
        {
            // Bind to the browser context so clicks survive redirects and new tabs.
            _binding = await _context.ExposeBindingAsync<string>(_bindingName, (source, json) =>
            {
                // Recording is restricted to approved hosts, including child frames.
                if (!_capturing || _stopping || json.Length > 8192 ||
                    !EnterprisePolicy.Load().AllowsUrl(source.Frame.Url)) return;
                try
                {
                    var action = JsonSerializer.Deserialize<CapturedAction>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (action?.Type == "Click" &&
                        !string.IsNullOrWhiteSpace(action.Target) &&
                        action.Target.Length <= 2048)
                        _pending.Enqueue(action);
                }
                catch (JsonException) { }
            });

            await _context.AddInitScriptAsync(BuildRecorderScript());
            var script = BuildRecorderScript();

            // Attach immediately to already-open pages and frames as well.
            foreach (var page in _context.Pages)
            {
                if (page.IsClosed) continue;
                foreach (var frame in page.Frames)
                {
                    try { await frame.EvaluateAsync(script); }
                    catch (PlaywrightException) { }
                }
            }

            CaptureToggle.Content = "Pause recording";
            CaptureStatus.Text = "Recording every click. Use the browser, then come back to Save clicks.";
        }
        catch (Exception ex)
        {
            _capturing = false;
            _drainTimer.Stop();
            CaptureToggle.IsEnabled = false;
            CaptureStatus.Text = "Click recording could not start. You can still add steps manually.";
            MessageBox.Show(this, ex.Message, "Teach couldn't start",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _initializing = false;
        }
    }

    private string BuildRecorderScript() => $$"""
        (() => {
          const binding = '{{_bindingName}}';
          const stateName = '{{_stateName}}';
          if (window[stateName] || typeof window[binding] !== 'function') return;

          const cssPath = (element) => {
            if (!(element instanceof Element)) return '';
            const safeId = (s) => typeof s === 'string' && /^[A-Za-z][A-Za-z0-9_-]{0,79}$/.test(s);
            const testId = element.getAttribute('data-testid');
            if (safeId(testId)) return '[data-testid="' + testId + '"]';
            if (safeId(element.id)) return '#' + CSS.escape(element.id);
            const parts = [];
            let el = element;
            for (let i = 0; el && i < 8; i++, el = el.parentElement) {
              if (safeId(el.id)) {
                parts.unshift('#' + CSS.escape(el.id));
                break;
              }
              let name = el.tagName.toLowerCase();
              const parent = el.parentElement;
              if (parent) {
                const siblings = Array.from(parent.children).filter(n => n.tagName === el.tagName);
                if (siblings.length > 1) name += ':nth-of-type(' + (siblings.indexOf(el) + 1) + ')';
              }
              parts.unshift(name);
            }
            return parts.join(' > ');
          };

          let lastPointerTime = 0;
          const record = (event) => {
            if (!event.isTrusted) return;
            const node = event.target;
            if (!(node instanceof Element)) return;
            const element = node.closest('a,button,input,select,textarea,label,[role="button"],[role="link"]') || node;
            const target = cssPath(element);
            if (!target) return;

            // Labels and textContent can contain names, ticket IDs or secrets.
            // Record only the HTML element type and a selector, never page text.
            const label = element.tagName.toLowerCase();
            const send = window[binding];
            if (typeof send === 'function') {
              void send(JSON.stringify({ type: 'Click', target, label })).catch(() => {});
            }
          };

          const onPointer = (event) => {
            if (event.button !== 0 || !event.isPrimary) return;
            lastPointerTime = Date.now();
            record(event);
          };
          const onClick = (event) => {
            // Keyboard-activated clicks lack a preceding pointer event.
            if (Date.now() - lastPointerTime >= 700) record(event);
          };
          document.addEventListener('pointerdown', onPointer, true);
          document.addEventListener('click', onClick, true);
          window[stateName] = () => {
            document.removeEventListener('pointerdown', onPointer, true);
            document.removeEventListener('click', onClick, true);
            delete window[stateName];
          };
        })()
        """;

    private void ToggleCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping || _initializing || _saving || _binding is null) return;
        _capturing = !_capturing;
        CaptureToggle.Content = _capturing ? "Pause recording" : "Resume recording";
        DrainActions();
        CaptureStatus.Text = _capturing
            ? "Recording every click…"
            : $"Paused — {RecordedSteps.Count} click(s) captured.";
    }

    private void DrainActions()
    {
        var changed = false;
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

        if (!changed) return;
        RefreshList();
        CaptureStatus.Text = $"Recorded {RecordedSteps.Count} click(s). Click Save when you're done.";
    }

    private void CaptureUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var page = _context.Pages.LastOrDefault(p => !p.IsClosed) ?? _page;
            var url = page.Url;
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
        CaptureStatus.Text = "No clicks recorded yet. Try clicking something on the website.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping || _saving || _initializing) return;
        _saving = true;
        SaveStepsButton.IsEnabled = false;
        try
        {
            await Task.Delay(450);
            DrainActions();
            if (RecordedSteps.Count == 0)
            {
                CaptureStatus.Text = "Nothing was recorded. Click on the website, then try Save again.";
                return;
            }
            Saved = true;
            Close();
        }
        finally
        {
            _saving = false;
            if (!Saved && !_stopping) SaveStepsButton.IsEnabled = true;
        }
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
        var cleanup = "() => { const key = '" + _stateName + "'; if (typeof window[key] === 'function') window[key](); }";
        try
        {
            foreach (var page in _context.Pages)
            {
                if (page.IsClosed) continue;
                foreach (var frame in page.Frames)
                {
                    try { await frame.EvaluateAsync(cleanup); }
                    catch (PlaywrightException) { }
                }
            }
        }
        catch (PlaywrightException) { }

        if (_binding is not null)
        {
            try { await _binding.DisposeAsync(); }
            catch (Exception) { }
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
