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
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
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

    private void SelectType(string type)
    {
        for (int i = 0; i < TypeCombo.Items.Count; i++)
        {
            if ((TypeCombo.Items[i] as ComboBoxItem)?.Content?.ToString() == type)
            {
                TypeCombo.SelectedIndex = i;
                return;
            }
        }
        TypeCombo.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var type = (TypeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Navigate";
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
