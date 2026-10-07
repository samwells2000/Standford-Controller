using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StanfordController.App.Infrastructure;
using StanfordController.App.Services;
using StanfordController.Presentation.Help;
using StanfordController.Presentation.Services;
using StanfordController.Presentation.ViewModels;

namespace StanfordController.App;

public partial class App : Application
{
    /// <summary>True for the portable build (published with -p:PortableBuild=true).</summary>
    public static bool IsPortableBuild { get; } = typeof(App).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "PortableBuild" && a.Value == "true");

    public static AppPaths Paths { get; private set; } = AppPaths.ForUser();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ClickAwayBehavior.Install();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Start(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? [];
        var smokeIndex = Array.IndexOf(args, "--smoke-test");
        var smokeDirectory = smokeIndex < 0
            ? null
            : smokeIndex + 1 < args.Length && !args[smokeIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? Path.GetFullPath(args[smokeIndex + 1])
                : Path.Combine(Path.GetTempPath(), "tempo-smoke-test");

        string? startupMessage = null;
        if (smokeDirectory is not null)
        {
            // The smoke test must never touch the user's configurations or preferences.
            var data = Path.Combine(smokeDirectory, "data");
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }

            Directory.CreateDirectory(data);
            Paths = new AppPaths(data, IsPortableBuild);
        }
        else
        {
            if (IsPortableBuild)
            {
                var programDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                Paths = AppPaths.ForPortable(programDirectory) ?? AppPaths.ForUser();
                if (!Paths.IsPortable)
                {
                    startupMessage = $"The folder beside the program cannot be written, so files are kept in {Paths.DataDirectory} instead.";
                }
            }

            if (Paths.MigrateFromStrobe())
            {
                startupMessage ??= $"Your configurations and preferences from Strobe, this program's previous name, have been copied from {Paths.PreviousDataDirectory}.";
            }
            else if (Paths.MigrateLegacyInstruments())
            {
                startupMessage ??= "Your saved instruments from the previous version have been copied into the Default configuration.";
            }
        }

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (SmokeTest.Current is { } smokeTest)
            {
                smokeTest.Fail(e.Exception);
            }
            else
            {
                if (DialogService.Current is { } dialogs)
                {
                    _ = dialogs.ShowErrorAsync(AppPaths.AppName,
                        $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe program will keep running, but you may want to reconnect to your instruments.");
                }
            }

            e.Handled = true;
        };

        var settingsStore = new AppSettingsStore(Paths.SettingsPath);
        var settings = settingsStore.Load();
        ThemeManager.Apply(settings.Theme);

        var dialogs = new DialogService(Paths);
        var viewModel = new MainViewModel(Paths.DefaultConfigurationPath, dialogs, settingsStore)
        {
            VersionText = $"Version {AppInfo.Version}",
            IsPortable = Paths.IsPortable,
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Theme))
            {
                ThemeManager.Apply(viewModel.Theme);
            }
        };

        var window = new MainWindow { DataContext = viewModel };
        if (smokeDirectory is null)
        {
            window.RestorePlacement(settings.Window);
        }

        dialogs.Owner = window;
        desktop.MainWindow = window;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        var started = false;
        window.Opened += async (_, _) =>
        {
            if (started)
            {
                return;
            }

            started = true;
            await viewModel.LoadAsync();
            if (startupMessage is not null)
            {
                viewModel.StatusMessage = startupMessage;
            }

            if (smokeDirectory is not null)
            {
                await new SmokeTest(window, viewModel, dialogs, smokeDirectory, desktop).RunAsync();
                return;
            }

            await viewModel.ConnectStartupInstrumentsAsync();
        };
    }
}
