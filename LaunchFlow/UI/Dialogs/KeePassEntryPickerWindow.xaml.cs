using System.Windows;
using System.Windows.Controls;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class KeePassEntryPickerWindow : Window
{
    private readonly IReadOnlyList<string> _entries;
    public string? SelectedPath { get; private set; }

    public KeePassEntryPickerWindow(IReadOnlyList<string> entries)
    {
        _entries = entries;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            DarkWindowChrome.Apply(this);
            SearchBox.Focus();
        };
        RefreshList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void RefreshList()
    {
        if (EntryList is null || SearchBox is null) return;
        var query = SearchBox.Text.Trim();
        EntryList.ItemsSource = _entries
            .Where(entry => entry.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not string path) return;
        SelectedPath = path;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
