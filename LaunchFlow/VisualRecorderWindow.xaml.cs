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
    private readonly TeachRecorder _recorder;
    private readonly DispatcherTimer _drainTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _started;
    private bool _stopping;

    public bool Saved { get; private set; }
    public List<WorkflowStep> RecordedSteps { get; } = new();

    public VisualRecorderWindow(IPage page)
    {
        _page = page;
        _recorder = new TeachRecorder(page);
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
        if (_stopping || _started) return;
        _started = true;
        CaptureToggle.IsEnabled = false;
        try
        {
            if (!CheckPolicy()) return;
            await _recorder.SynchronizeAsync();
            if (_stopping || !CheckPolicy()) return;
            _drainTimer.Start();
            ShowCaptureState();
        }
        catch (Exception)
        {
            if (_stopping || !CheckPolicy()) return;
            _recorder.Pause();
            CaptureToggle.Content = "Retry recording";
            CaptureStatus.Text = "Could not start click recording. You can still add a step manually.";
            MessageBox.Show(this, "Could not start click recording. Try again or cancel.", "Teach couldn't start", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (!_stopping) CaptureToggle.IsEnabled = !_page.IsClosed;
        }
    }

    private async void ToggleCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping) return;
        CaptureToggle.IsEnabled = false;
        try
        {
            if (_recorder.IsCapturing) _recorder.Pause();
            else
            {
                // Clear revoked displayed steps before an explicit fresh resume.
                CheckPolicy();
                _recorder.Resume();
            }
            if (!CheckPolicy()) return;
            DrainActions();
            await _recorder.SynchronizeAsync();
            if (_stopping || !CheckPolicy()) return;
            _drainTimer.Start();
            ShowCaptureState();
        }
        catch (Exception)
        {
            if (_stopping || !CheckPolicy()) return;
            _recorder.Pause();
            CaptureToggle.Content = "Retry recording";
            CaptureStatus.Text = "Recording is paused. Reopen the browser or try again.";
        }
        finally
        {
            if (!_stopping) CaptureToggle.IsEnabled = !_page.IsClosed;
        }
    }

    private void ShowCaptureState()
    {
        if (!CheckPolicy()) return;
        if (_page.IsClosed)
        {
            CaptureToggle.IsEnabled = false;
            CaptureStatus.Text = "Browser page closed — save captured clicks or cancel, then start a new Teach session.";
            return;
        }
        CaptureToggle.Content = _recorder.IsCapturing ? "Pause recording" : "Resume recording";
        CaptureStatus.Text = _recorder.IsCapturing
            ? "Recording safe actions — " + RecordedSteps.Count + " step(s) captured."
            : "Paused — " + RecordedSteps.Count + " step(s) captured.";
        if (_recorder.UnsupportedTargetObserved)
            CaptureStatus.Text += " Some keys or actions were skipped (sensitive form, unsupported shortcut, popup or frame). Add safe actions manually.";
    }

    private void DrainActions()
    {
        if (_stopping || !CheckPolicy()) return;
        var actions = _recorder.DrainActions();
        if (!CheckPolicy()) return;
        foreach (var action in actions)
            RecordedSteps.Add(new WorkflowStep
            {
                Type = action.Type,
                Target = action.Target,
                Description = action.Type == "Press" ? "Press " + action.Target : "Click " + action.Target
            });
        if (actions.Count > 0) RefreshList();
        if (actions.Count > 0 || _recorder.UnsupportedTargetObserved || _page.IsClosed) ShowCaptureState();
    }

    private void CaptureUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping || !CheckPolicy()) return;
        try
        {
            var page = _page.Context.Pages.LastOrDefault(p => !p.IsClosed) ?? _page;
            var url = page.Url;
            if (!CheckPolicy(url)) return;
            AutomationEngine.ValidateUrl(url);
            AddStep("Navigate", url, "Open " + url);
        }
        catch (Exception)
        {
            MessageBox.Show(this, "Open an allowed website in the browser first.",
                "No page yet", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AddNavigate_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Navigate", Target = "https://", Description = "Open a page" });
    private void AddClick_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Click", Target = "button", Description = "Click" });
    private void AddWait_Click(object sender, RoutedEventArgs e) =>
        EditNew(new WorkflowStep { Type = "Wait", Target = "1500", Description = "Wait" });

    private void QuickWait_Click(object sender, RoutedEventArgs e) =>
        AddStep("Wait", "1000", "Wait 1 second for slow pages");

    private void SelectAll_Click(object sender, RoutedEventArgs e) => StepList.SelectAll();

    private void EditSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping || !CheckPolicy() || StepList.SelectedItems.Count != 1 ||
            StepList.SelectedItem is not WorkflowStep selected) return;
        DrainActions();
        var index = RecordedSteps.IndexOf(selected);
        if (index < 0) return;
        var editor = new StepEditorWindow(selected) { Owner = this };
        if (editor.ShowDialog() != true || _stopping || !CheckPolicy()) return;
        if (index >= RecordedSteps.Count || !ReferenceEquals(RecordedSteps[index], selected)) return;
        if (editor.Result.Type == "Navigate" && !CheckPolicy(editor.Result.Target)) return;
        RecordedSteps[index] = editor.Result;
        RefreshList();
        StepList.SelectedIndex = index;
    }

    private void EditNew(WorkflowStep step)
    {
        if (_stopping || !CheckPolicy()) return;
        var editor = new StepEditorWindow(step) { Owner = this };
        if (editor.ShowDialog() == true && !_stopping)
            AddStep(editor.Result.Type, editor.Result.Target,
                editor.Result.Description ?? editor.Result.Type + " " + editor.Result.Target,
                editor.Result.Value);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping || !CheckPolicy()) return;
        var selected = StepList.SelectedItems.Cast<WorkflowStep>().ToArray();
        DrainActions();
        if (selected.Length == 0) return;
        if (selected.Length > 1 && MessageBox.Show(this,
            $"Delete {selected.Length} selected actions?", "Delete recorded actions",
            MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var step in selected) RecordedSteps.Remove(step);
        RefreshList();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping || !CheckPolicy()) return;
        RecordedSteps.Clear();
        _recorder.DrainActions();
        RefreshList();
        CaptureStatus.Text = "No clicks saved yet. Try clicking something on the website.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping || !CheckPolicy()) return;
        // Freeze browser callbacks before draining; no delayed continuation may save after Cancel/Close.
        var wasCapturing = _recorder.IsCapturing;
        _recorder.Pause();
        DrainActions();
        if (!CheckPolicy()) return;
        if (RecordedSteps.Count == 0)
        {
            if (wasCapturing) _recorder.Resume();
            CaptureStatus.Text = "Nothing was recorded yet. Click a button or press an allowed navigation key in the website.";
            _ = SynchronizeAfterEmptySaveAsync();
            return;
        }
        _stopping = true;
        _recorder.Dispose();
        Saved = true;
        Close();
    }

    private async Task SynchronizeAfterEmptySaveAsync()
    {
        try
        {
            if (_stopping || !CheckPolicy()) return;
            await _recorder.SynchronizeAsync();
            if (!_stopping) ShowCaptureState();
        }
        catch (Exception)
        {
            if (_stopping) return;
            _recorder.Pause();
            ShowCaptureState();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _stopping = true;
        _recorder.Dispose();
        _drainTimer.Stop();
        if (!Saved) RecordedSteps.Clear();
        try { await _recorder.SynchronizeAsync(); }
        catch (Exception) { /* Cleanup must not escape an async window event. */ }
    }

    private void AddStep(string type, string target, string description, string? value = null)
    {
        var urls = type == "Navigate" ? new[] { target } : Array.Empty<string>();
        if (_stopping || !CheckPolicy(urls)) return;
        DrainActions();
        if (!CheckPolicy(urls)) return;
        RecordedSteps.Add(new WorkflowStep
        {
            Type = type,
            Target = target,
            Value = value ?? "",
            Description = description
        });
        RefreshList();
    }

    private bool CheckPolicy(params string[] additionalUrls)
    {
        if (_stopping) return false;
        var urls = RecordedSteps.Where(step => step.Type == "Navigate").Select(step => step.Target)
            .Concat(additionalUrls).ToArray();
        if (_recorder.CheckPolicy(urls)) return true;
        _recorder.Pause();
        RecordedSteps.Clear();
        RefreshList();
        CaptureToggle.Content = "Resume recording";
        CaptureStatus.Text = "Teach recording is disabled by your organization. Unsaved steps were cleared.";
        return false;
    }

    private void RefreshList()
    {
        StepList.ItemsSource = null;
        StepList.ItemsSource = RecordedSteps;
        if (RecordedSteps.Count > 0) StepList.ScrollIntoView(RecordedSteps[^1]);
    }
}
