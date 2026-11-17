using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class SecretPromptWindow : Window
{
    public string Secret { get; private set; } = "";

    public SecretPromptWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        Loaded += (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            SecretBox.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Secret = SecretBox.Password;
        SecretBox.Clear();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SecretBox.Clear();
        Secret = "";
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        SecretBox.Clear();
        base.OnClosed(e);
    }
}
