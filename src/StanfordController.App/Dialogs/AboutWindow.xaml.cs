using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using StanfordController.Core.Profiles;

namespace StanfordController.App.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {App.Version} · .NET {Environment.Version}";
        DataFolder.Text = ProfileStore.DefaultDirectory;
        ReleasesLink.NavigateUri = new Uri(App.RepositoryUrl + "/releases");
        ReadmeLink.NavigateUri = new Uri(App.RepositoryUrl + "#readme");
        ReferenceLink.NavigateUri = new Uri(App.RepositoryUrl + "/blob/main/docs/DG645-Command-Reference.md");
        Shortcuts.ItemsSource = new Dictionary<string, string>
        {
            ["Ctrl+N"] = "Add an instrument",
            ["Ctrl+F"] = "Find instruments on the network",
            ["Enter / Ctrl+K"] = "Connect the selected instrument",
            ["Ctrl+Shift+K"] = "Disconnect",
            ["F2 / Delete"] = "Edit / remove the selected instrument",
            ["F5"] = "Read every setting from the instrument",
            ["Ctrl+T"] = "Single-shot trigger (*TRG)",
            ["Ctrl+S / Ctrl+O"] = "Save / load a settings file",
            ["Ctrl+L"] = "Show or hide the communication log",
            ["Enter / Esc"] = "Apply / revert an edited field",
        };
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ProfileStore.DefaultDirectory);
        Open(ProfileStore.DefaultDirectory);
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open {target}:\n{ex.Message}", "DG645 Controller", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
