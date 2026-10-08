using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using StanfordController.Presentation.Help;
using StanfordController.Presentation.Services;

namespace StanfordController.App.Infrastructure;

/// <summary>Help ▸ Quick reference card: the two-sided card built into the program, opened in the default web browser.</summary>
internal static class QuickReference
{
    private const string ResourceName = "Tempo.QuickReference.pdf";
    private static LocalDocumentServer? _server;

    /// <summary>The card's PDF (rebuilt with docs/quick-reference/build.mjs).</summary>
    public static byte[] Load()
    {
        using var resource = typeof(QuickReference).Assembly.GetManifestResourceStream(ResourceName)
                             ?? throw new FileNotFoundException("The quick reference card is missing from this build.");
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Opens the card in the default web browser, which shows it ready to print. Without a browser it is saved to the
    /// temporary folder and opened in the PDF viewer instead. Returns what happened, for the status bar.
    /// </summary>
    public static async Task<string> OpenAsync(TopLevel window)
    {
        try
        {
            var card = Load();
            try
            {
                // Served for as long as the program runs, so the browser can reload it.
                _server ??= new LocalDocumentServer(HelpGuide.QuickReferenceFile, card, "application/pdf");
                if (await window.Launcher.LaunchUriAsync(_server.Address))
                {
                    return "The quick reference card is open in your web browser, ready to print.";
                }
            }
            catch (SocketException)
            {
                // Not allowed to serve it on this computer: fall back to the file.
            }

            var path = Path.Combine(Path.GetTempPath(), HelpGuide.QuickReferenceFile);
            await File.WriteAllBytesAsync(path, card);
            return await window.Launcher.LaunchFileInfoAsync(new FileInfo(path))
                ? "The quick reference card is open, ready to print."
                : $"The quick reference card is saved at {path}; open it with a web browser or PDF viewer.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The quick reference card could not be opened: {ex.Message}";
        }
    }
}
