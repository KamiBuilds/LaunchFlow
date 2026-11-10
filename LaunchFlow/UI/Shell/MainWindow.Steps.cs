using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Win32;
using Microsoft.Playwright;
using Forms = System.Windows.Forms;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace LaunchFlow;

/// <summary>Step editing and safe recorded-action list management.</summary>
public partial class MainWindow
{
    private void RefreshSteps()
    {
        if (_editorSession.Selection is null) return;
        StepList.ItemsSource = null;
        StepList.ItemsSource = _editorSession.Selection.Steps;
    }

    private void SelectAllSteps_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is not null) StepList.SelectAll();
    }

    private void QuickWaitStep_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is null) return;
        _editorSession.Selection.Steps.Add(new WorkflowStep
        {
            Type = "Wait", Target = "1000", Description = "Wait 1 second for the page"
        });
        RefreshSteps();
        StepList.SelectedIndex = _editorSession.Selection.Steps.Count - 1;
        MarkUnsavedChanges();
    }

    private bool IsCurrentStepEditorOwner(WebsiteWorkflow workflow, long revision) =>
        revision == _workspaceRevision &&
        ReferenceEquals(_editorSession.Selection, workflow) &&
        _configuration.Workflows.Any(item => ReferenceEquals(item, workflow)) &&
        !_windowsLocked && !_vaultLockInProgress && !_exiting &&
        VaultDatabase.Current.IsUnlocked;

    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is not { } workflow) return;
        var revision = _workspaceRevision;
        var editor = new StepEditorWindow(new WorkflowStep { Type = "Navigate", Target = "https://" }) { Owner = this };
        if (!ShowTrackedStepEditor(editor)) return;
        if (!IsCurrentStepEditorOwner(workflow, revision)) return;
        workflow.Steps.Add(editor.Result);
        RefreshSteps();
        MarkUnsavedChanges();
    }


    private void AddStep_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is not { } workflow) return;
        var revision = _workspaceRevision;
        var editor = new StepEditorWindow { Owner = this };
        if (!ShowTrackedStepEditor(editor) || !IsCurrentStepEditorOwner(workflow, revision)) return;
        workflow.Steps.Add(editor.Result);
        RefreshSteps();
        MarkUnsavedChanges();
    }

    private void EditStep_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is null || StepList.SelectedItems.Count != 1 ||
            StepList.SelectedItem is not WorkflowStep current) return;
        var workflow = _editorSession.Selection;
        var revision = _workspaceRevision;
        int index = workflow.Steps.IndexOf(current);
        if (index < 0) return;
        var editor = new StepEditorWindow(current) { Owner = this };
        if (!ShowTrackedStepEditor(editor)) return;
        // ShowDialog pumps WPF messages. Import can replace the entire workflow
        // with an object having the same GUID and step count while it is open.
        if (!IsCurrentStepEditorOwner(workflow, revision) ||
            !WorkflowEditGuard.IsCurrent(_configuration, _editorSession.Selection, workflow, current,
                index, revision, _workspaceRevision) ||
            !ReferenceEquals(StepList.SelectedItem, current))
        {
            AppendLog("Step edit canceled because the website changed while its editor was open.");
            return;
        }
        workflow.Steps[index] = editor.Result;
        RefreshSteps();
        StepList.SelectedIndex = index;
        MarkUnsavedChanges();
    }

    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (_editorSession.Selection is null || StepList.SelectedItems.Count == 0) return;
        var workflow = _editorSession.Selection;
        var revision = _workspaceRevision;
        var selected = StepList.SelectedItems.Cast<WorkflowStep>().ToArray();
        if (selected.Length > 1 && MessageBox.Show(this,
            $"Delete {selected.Length} selected actions? This takes effect when you save.",
            "Delete actions", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        // Confirmation pumps the dispatcher; refuse if selection/import replaced
        // the owned workflow or any chosen step is no longer in that list.
        if (!IsCurrentStepEditorOwner(workflow, revision) ||
            !ReferenceEquals(_editorSession.Selection, workflow) ||
            selected.Any(step => !workflow.Steps.Contains(step)))
        {
            AppendLog("Step delete canceled because the website changed while confirmation was open.");
            return;
        }
        foreach (var step in selected) workflow.Steps.Remove(step);
        RefreshSteps();
        MarkUnsavedChanges();
    }

    private void MoveStepUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);
    private void MoveStepDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);
    private void MoveStep(int offset)
    {
        if (_editorSession.Selection is null || StepList.SelectedItems.Count != 1 ||
            StepList.SelectedItem is not WorkflowStep current) return;
        int index = _editorSession.Selection.Steps.IndexOf(current);
        int dest = index + offset;
        if (dest < 0 || dest >= _editorSession.Selection.Steps.Count) return;
        _editorSession.Selection.Steps.RemoveAt(index);
        _editorSession.Selection.Steps.Insert(dest, current);
        RefreshSteps(); StepList.SelectedIndex = dest;
        MarkUnsavedChanges();
    }

}
