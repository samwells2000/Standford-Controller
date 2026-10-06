using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using StanfordController.App.Infrastructure;
using StanfordController.App.Services;
using StanfordController.Core.Profiles;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App;

public partial class App : Application
{
    /// <summary>"1.0.42" — the build number comes from the CI run that produced the release.</summary>
    public static string Version { get; } = Assembly.GetEntryAssembly()?.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "1.0.0";

    public const string RepositoryUrl = "https://github.com/samwells2000/Standford-Controller";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var smokeTestIndex = Array.IndexOf(e.Args, "--smoke-test");
        var smokeTestDirectory = smokeTestIndex >= 0
            ? (e.Args.Length > smokeTestIndex + 1 ? e.Args[smokeTestIndex + 1] : Path.Combine(Path.GetTempPath(), "dg645-smoke-test"))
            : null;

        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        // The smoke test must never touch the user's saved instruments or preferences.
        var dataDirectory = smokeTestDirectory ?? ProfileStore.DefaultDirectory;
        if (smokeTestDirectory is not null)
        {
            Directory.CreateDirectory(smokeTestDirectory);
            foreach (var file in new[] { "instruments.json", "settings.json" })
            {
                File.Delete(Path.Combine(smokeTestDirectory, file));
            }
        }

        var store = new ProfileStore(Path.Combine(dataDirectory, "instruments.json"));
        var settings = new AppSettingsStore(Path.Combine(dataDirectory, "settings.json"));
        var dialogs = new DialogService();
        var viewModel = new MainViewModel(store, dialogs, settings) { VersionText = $"Version {Version}" };
        viewModel.Load();

        ThemeManager.Apply(viewModel.Theme);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.Theme))
            {
                ThemeManager.Apply(viewModel.Theme);
            }
        };

        var window = new MainWindow { DataContext = viewModel };
        dialogs.Owner = window;
        MainWindow = window;

        if (smokeTestDirectory is not null)
        {
            var smokeTest = new SmokeTest(window, viewModel, smokeTestDirectory);
            smokeTest.Install();
            window.Show();
            _ = smokeTest.RunAsync();
            return;
        }

        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (SmokeTest.Current is { } smokeTest)
        {
            smokeTest.Fail(e.Exception);
            e.Handled = true;
            return;
        }

        MessageBox.Show(
            MainWindow,
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe program will keep running, but you may want to reconnect to your instruments.",
            MainViewModel.AppName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
