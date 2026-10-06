using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StanfordController.Presentation.Help;
using StanfordController.Presentation.Services;

namespace StanfordController.App.Dialogs;

public partial class AboutWindow : Window
{
    private readonly AppPaths _paths = AppPaths.ForUser();

    public AboutWindow()
    {
        InitializeComponent();
    }

    public AboutWindow(AppPaths paths)
        : this()
    {
        _paths = paths;
        VersionText.Text = $"Version {AppInfo.Version}{(paths.IsPortable ? " · portable" : string.Empty)} · .NET {Environment.Version} · {Environment.OSVersion.Platform}";
        DataKind.Text = paths.IsPortable
            ? "This portable copy keeps its configurations and preferences beside the program:"
            : "Configurations and preferences are kept in:";
        DataFolder.Text = paths.DataDirectory;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(_paths.DataDirectory));
    }

    private void OnReleases(object? sender, RoutedEventArgs e) => Open(AppInfo.ReleasesUrl);

    private void OnSource(object? sender, RoutedEventArgs e) => Open(AppInfo.RepositoryUrl);

    private void OnReference(object? sender, RoutedEventArgs e) => Open(AppInfo.ResolveExternalLink("DG645-Command-Reference.md")!);

    private void Open(string url) => Launcher.LaunchUriAsync(new Uri(url));
}
