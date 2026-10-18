using System.Text.Json;
using System.Text.Json.Serialization;

namespace StanfordController.Presentation.Services;

public enum AppTheme
{
    /// <summary>Follow the operating system's light/dark setting.</summary>
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

/// <summary>Per-user preferences (settings.json in the data folder).</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public WindowPlacement? Window { get; set; }

    public Guid? SelectedInstrumentId { get; set; }

    public bool ShowLog { get; set; } = true;

    /// <summary>Play the system's alert sound when an instrument reports a fault or its connection drops.</summary>
    public bool AlertSound { get; set; } = true;

    /// <summary>The configuration file open last time; reopened at startup.</summary>
    public string? ConfigurationPath { get; set; }

    /// <summary>Connect every instrument when a configuration opens, not only those marked to connect on opening.</summary>
    public bool ConnectAllOnOpen { get; set; }

    /// <summary>Recently opened configuration files, most recent first.</summary>
    public List<string> RecentConfigurations { get; set; } = [];

    /// <summary>Where the audit trail is written, or null for the "Audit trail" folder in the data folder.</summary>
    public string? AuditFolder { get; set; }
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
