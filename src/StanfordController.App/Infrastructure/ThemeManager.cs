using Avalonia;
using Avalonia.Styling;
using StanfordController.Presentation.Services;

namespace StanfordController.App.Infrastructure;

/// <summary>Switches between the light and dark colour dictionaries. "System" follows the operating system.</summary>
public static class ThemeManager
{
    public static void Apply(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}
