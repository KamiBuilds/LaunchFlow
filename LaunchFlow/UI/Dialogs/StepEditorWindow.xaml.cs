using System.Windows;
using System.Windows.Controls;
using LaunchFlow.Models;
using LaunchFlow.Services;
using MessageBox = System.Windows.MessageBox;

namespace LaunchFlow;

public partial class StepEditorWindow : Window
{
    public WorkflowStep Result { get; private set; } = new();

    public StepEditorWindow() : this(new WorkflowStep { Type = "Navigate", Target = "https://" })
    {
    }

    public StepEditorWindow(WorkflowStep existing)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            ApplyLanguage();
        };
        Result = new WorkflowStep
        {
            Type = existing.Type,
            Target = existing.Target,
            Value = existing.Value,
            Description = existing.Description
        };
        SelectType(Result.Type);
        TargetBox.Text = Result.Target;
        ValueBox.Text = Result.Value ?? "";
        DescriptionBox.Text = Result.Description ?? "";
    }

    private void ApplyLanguage()
    {
        string T(string value) => LocalizationService.Text(value);
        Title = T("Workflow step");
        Heading.Text = T("Workflow step");
        TypeLabel.Text = T("Type");
        TargetLabel.Text = T("Target (URL, CSS selector, key, or milliseconds)");
        ValueLabel.Text = T("Value (for Fill)");
        DescriptionLabel.Text = T("Description (optional)");
        SecretsWarning.Text = T("Never store passwords or access tokens as Fill values. Use the Passwords tab.");
        CancelButton.Content = T("Cancel");
        SaveButton.Content = T("Save");
        foreach (ComboBoxItem item in TypeCombo.Items)
            if (item.Tag is string action) item.Content = T(action);
    }

    private void SelectType(string type)
    {
        for (int i = 0; i < TypeCombo.Items.Count; i++)
        {
            if ((TypeCombo.Items[i] as ComboBoxItem)?.Tag?.ToString() == type)
            {
                TypeCombo.SelectedIndex = i;
                return;
            }
        }
        TypeCombo.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var type = (TypeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Navigate";
        var target = TargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "Target is required.", "Step", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (type is "Navigate" or "WaitForURL")
        {
            try { AutomationEngine.ValidateUrl(target); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Result = new WorkflowStep
        {
            Type = type,
            Target = target,
            Value = string.IsNullOrWhiteSpace(ValueBox.Text) ? null : ValueBox.Text,
            Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim()
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
