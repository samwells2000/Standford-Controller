using System.Runtime.InteropServices;

namespace StanfordController.App.Infrastructure;

/// <summary>The system's alert sound, played with a new alert when View ▸ Alert sound is on.</summary>
internal static class AlertSound
{
    private const uint IconHand = 0x10;
    private const uint IconExclamation = 0x30;

    public static void Play(bool error)
    {
        // Linux desktops have no common way to play a system sound; there the alert bar and the window asking for
        // attention have to do.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            MessageBeep(error ? IconHand : IconExclamation);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No sound, but the alert is still shown.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
}
