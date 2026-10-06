using System.Text.Json;
using System.Text.Json.Serialization;

namespace StanfordController.Presentation.Services;

public enum AppTheme
{
    /// <summary>Follow the Windows light/dark app setting.</summary>
    System,
    Light,
    Dark,
}

/// <summary>Where the main window was, so it reopens in the same place.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>Per-user preferences, stored next to the instrument list.</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public WindowPlacement? Window { get; set; }

    public Guid? SelectedInstrumentId { get; set; }

    public bool ShowLog { get; set; } = true;
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. A missing or unreadable file gives the defaults.</summary>
public sealed class AppSettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = path;

    public AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Preferences are not worth bothering the user about; start from the defaults.
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a window position is harmless; never interrupt the user for it.
        }
    }
}
