using System.Windows;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class TutorialWindow : Window
{
    private int _current;
    private static readonly (string Title, string Detail)[] Pages =
    {
        ("1. Add a website",
         "Click Add website. Give it a name, paste a URL, and choose an installed browser and screen. Most users can leave the advanced connection settings closed. Click Create website."),
        ("2. Login is optional",
         "If the website needs automatic sign-in, tick the login box and enter the details when creating it; they are stored encrypted. You can also select the website and manage its login later in Passwords. For sites without a login, leave this unchecked."),
        ("3. Recorded clicks are optional",
         "If you want LaunchFlow to press buttons after opening the page, select the website and click Record clicks. Click normally in the SAME browser tab, then Save actions. Passwords, typed text and codes are never recorded. Advanced controls stay under More actions."),
        ("4. Test a site, then run all",
         "Click Test this site to save changes and run just the selected website. If sign-in needs attention, use the nonblocking sign-in notice. Once it works, Run all opens every enabled website.")
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
            0 => "+ Add website",
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
