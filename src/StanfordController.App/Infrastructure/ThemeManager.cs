using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using StanfordController.Presentation.Services;

namespace StanfordController.App.Infrastructure;

/// <summary>
/// Switches the colour palette (merged dictionary slot 0) between light and dark, follows the Windows
/// "app mode" setting when the theme is System, and matches each window's title bar to the theme.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightPalette = new("Themes/Colors.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPalette = new("Themes/Colors.Dark.xaml", UriKind.Relative);
    private static AppTheme _requested = AppTheme.System;
    private static bool _initialized;

    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme theme)
    {
        _requested = theme;
        EnsureInitialized();
        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemUsesDarkTheme(),
        };

        var palettes = Application.Current.Resources.MergedDictionaries;
        if (palettes.Count > 0 && dark == IsDark && palettes[0].Source is not null && palettes[0].Source.OriginalString.EndsWith(dark ? "Dark.xaml" : "Light.xaml", StringComparison.Ordinal))
        {
            return;
        }

        IsDark = dark;
        palettes[0] = new ResourceDictionary { Source = dark ? DarkPalette : LightPalette };
        foreach (Window window in Application.Current.Windows)
        {
            ApplyTitleBar(window);
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        // Every window, including dialogs, gets a title bar that matches the theme.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_requested == AppTheme.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
            }
        };
    }

    /// <summary>Reads Windows' "Choose your default app mode" setting.</summary>
    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var value = IsDark ? 1 : 0;

        // Attribute 20 on Windows 10 20H1+ and Windows 11; 19 on earlier Windows 10 builds.
        if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
