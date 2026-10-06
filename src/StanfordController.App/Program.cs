using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;

namespace StanfordController.App;

internal static class Program
{
    public const string FontFamilyUri = "avares://Strobe/Assets/Fonts#IBM Plex Sans";

    /// <summary>
    /// Strobe [--smoke-test &lt;folder&gt;] [--headless]. The smoke test drives the real UI against the simulated
    /// instrument and saves screenshots; --headless runs it without a display.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var builder = BuildAvaloniaApp();
        if (args.Contains("--headless"))
        {
            builder = builder.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        }

        return builder.StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = FontFamilyUri })
            .LogToTrace();
}
