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
            await _recorder.SynchronizeAsync();
            if (_stopping) return;
            _drainTimer.Start();
            ShowCaptureState();
        }
        catch (Exception ex)
        {
            if (_stopping) return;
            _recorder.Pause();
            CaptureToggle.Content = "Retry recording";
            CaptureStatus.Text = "Could not start click recording. You can still add a step manually.";
            MessageBox.Show(this, ex.Message, "Teach couldn't start", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (_recorder.IsCapturing) _recorder.Pause();
        else _recorder.Resume();
        DrainActions();
        try
        {
            await _recorder.SynchronizeAsync();
            if (_stopping) return;
            _drainTimer.Start();
            ShowCaptureState();
        }
        catch (Exception ex)
        {
            if (_stopping) return;
            _recorder.Pause();
            CaptureToggle.Content = "Retry recording";
            CaptureStatus.Text = "Recording is paused. Reopen the browser or try again. " + ex.Message;
        }
        finally
        {
            if (!_stopping) CaptureToggle.IsEnabled = !_page.IsClosed;
        }
    }

    private void ShowCaptureState()
    {
        if (_page.IsClosed)
        {
            CaptureToggle.IsEnabled = false;
            CaptureStatus.Text = "Browser page closed — save captured clicks or cancel, then start a new Teach session.";
            return;
        }
        CaptureToggle.Content = _recorder.IsCapturing ? "Pause recording" : "Resume recording";
        CaptureStatus.Text = _recorder.IsCapturing
            ? "Recording the original tab's main page — " + RecordedSteps.Count + " click(s) captured."
            : "Paused — " + RecordedSteps.Count + " click(s) captured.";
        if (_recorder.UnsupportedTargetObserved)
            CaptureStatus.Text += " Popup, other-tab, frame or shadow-DOM clicks were not recorded.";
    }

    private void DrainActions()
    {
        if (_stopping) return;
        var actions = _recorder.Drain();
        foreach (var target in actions)
            RecordedSteps.Add(new WorkflowStep { Type = "Click", Target = target, Description = "Click " + target });
        if (actions.Count > 0) RefreshList();
        if (actions.Count > 0 || _recorder.UnsupportedTargetObserved || _page.IsClosed) ShowCaptureState();
    }

    private void CaptureUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping) return;
        try
        {
            var page = _page.Context.Pages.LastOrDefault(p => !p.IsClosed) ?? _page;
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
        if (_stopping) return;
        var editor = new StepEditorWindow(step) { Owner = this };
        if (editor.ShowDialog() == true && !_stopping)
            AddStep(editor.Result.Type, editor.Result.Target,
                editor.Result.Description ?? editor.Result.Type + " " + editor.Result.Target,
                editor.Result.Value);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping) return;
        var selected = StepList.SelectedItem as WorkflowStep;
        DrainActions();
        if (selected is not null)
        {
            RecordedSteps.Remove(selected);
            RefreshList();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping) return;
        RecordedSteps.Clear();
        _recorder.Drain();
        RefreshList();
        CaptureStatus.Text = "No clicks saved yet. Try clicking something on the website.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_stopping) return;
        // Freeze browser callbacks before draining; no delayed continuation may save after Cancel/Close.
        var wasCapturing = _recorder.IsCapturing;
        _recorder.Pause();
        DrainActions();
        if (RecordedSteps.Count == 0)
        {
            if (wasCapturing) _recorder.Resume();
            CaptureStatus.Text = "Nothing was recorded yet. Click a button on the website, then try Save clicks.";
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
        try { await _recorder.SynchronizeAsync(); }
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
        catch (PlaywrightException) { /* The browser may already be closed. */ }
    }

    private void AddStep(string type, string target, string description, string? value = null)
    {
        if (_stopping) return;
        DrainActions();
        RecordedSteps.Add(new WorkflowStep
        {
            Type = type,
            Target = target,
            Value = value ?? "",
            Description = description
        });
        RefreshList();
    }

    private void RefreshList()
    {
        StepList.ItemsSource = null;
        StepList.ItemsSource = RecordedSteps;
        if (RecordedSteps.Count > 0) StepList.ScrollIntoView(RecordedSteps[^1]);
    }
}
