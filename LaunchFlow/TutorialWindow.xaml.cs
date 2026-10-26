using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class TutorialWindow : Window
{
    private int _current;
    private static readonly (string Title, string Detail)[] Pages =
    {
        ("1. Add a website",
         "Click + New. Enter the website address, choose your browser, and pick the screen where it should open. Click Create."),
        ("2. Save your login (optional)",
         "Open Passwords to save your username and password in the encrypted vault. LaunchFlow can use these details when you click Run all. You never need to teach or record a password."),
        ("3. Show it what to click",
         "Select your website in Workflows, then choose Teach clicks. Recording starts automatically. Move either window, click buttons on the website, and come back to Save clicks. This is for buttons AFTER login."),
        ("4. Test it",
         "Click Test this site to try one website, or Run all to open every enabled website. If a login needs extra approval, finish it yourself. Open Help anytime to see these steps again.")
    };

    public TutorialWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => DarkWindowChrome.Apply(this);
        UpdateView();
    }

    private void UpdateView()
    {
        ProgressLabel.Text = "Step " + (_current + 1) + " of " + Pages.Length;
        StepHeading.Text = Pages[_current].Title;
        StepDetails.Text = Pages[_current].Detail;
        BackButton.IsEnabled = _current > 0;
        NextButton.Content = _current == Pages.Length - 1 ? "Finish" : "Next";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_current > 0) _current--;
        UpdateView();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_current == Pages.Length - 1) { Close(); return; }
        _current++;
        UpdateView();
    }
}
