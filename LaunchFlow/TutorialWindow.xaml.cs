using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class TutorialWindow : Window
{
    private int _current;
    private static readonly (string Title, string Detail)[] Pages =
    {
        ("1. Add a website",
         "In LaunchFlow, click + New website. Enter the start URL, choose your browser and monitor, then select Create. You can click in LaunchFlow while this guide stays open."),
        ("2. Save your login (optional)",
         "For a site requiring a login, open Passwords and save your credentials in the encrypted vault. Leave No login required selected for local dashboards. Never record, screenshot, or store a password as a step."),
        ("3. Show it what to click",
         "Select the website, then Record clicks. Click permitted buttons or press Enter/Tab/arrow keys in the browser. Text and passwords are not recorded. Add Wait +1s for slow pages, then Save actions. You can edit or select multiple actions afterward."),
        ("4. Test it",
         "Click Test this site to run one website, or Run all for your enabled sites. Changes awaiting Save are marked above the editor. If login needs approval, complete it personally in the browser.")
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
        ExampleTitle.Text = _current switch
        {
            0 => "1 · Websites",
            1 => "2 · Passwords",
            2 => "3 · Record actions",
            _ => "4 · Test and save"
        };
        ExampleWebsite.Text = _current switch
        {
            0 => "New website",
            1 => "Account stored in encrypted vault",
            2 => "Click → Wait → Press Enter",
            _ => "Enabled websites"
        };
        ExampleAction.Text = _current switch
        {
            0 => "+ New website",
            1 => "Save credentials",
            2 => "● Record clicks",
            _ => "▶ Test this site / Run all"
        };
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
