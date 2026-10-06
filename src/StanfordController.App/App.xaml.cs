using System.IO;
using System.Windows;
using System.Windows.Threading;
using StanfordController.App.Services;
using StanfordController.Core.Profiles;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var smokeTestIndex = Array.IndexOf(e.Args, "--smoke-test");
        var smokeTestDirectory = smokeTestIndex >= 0
            ? (e.Args.Length > smokeTestIndex + 1 ? e.Args[smokeTestIndex + 1] : Path.Combine(Path.GetTempPath(), "dg645-smoke-test"))
            : null;

        DispatcherUnhandledException += OnUnhandledException;

        // The smoke test must never touch the user's saved instruments.
        var store = smokeTestDirectory is null
            ? new ProfileStore(ProfileStore.DefaultPath)
            : new ProfileStore(Path.Combine(smokeTestDirectory, "instruments.json"));

        var dialogs = new DialogService();
        var viewModel = new MainViewModel(store, dialogs);
        var window = new MainWindow { DataContext = viewModel };
        dialogs.Owner = window;
        MainWindow = window;

        if (smokeTestDirectory is not null)
        {
            Directory.CreateDirectory(smokeTestDirectory);
            if (File.Exists(store.FilePath))
            {
                File.Delete(store.FilePath);
            }

            var smokeTest = new SmokeTest(window, viewModel, smokeTestDirectory);
            smokeTest.Install();
            viewModel.Load();
            window.Show();
            _ = smokeTest.RunAsync();
            return;
        }

        viewModel.Load();
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
            "Stanford DG645 Controller",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
