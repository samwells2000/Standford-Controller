using StanfordController.Core.Profiles;

namespace StanfordController.Presentation.Services;

/// <summary>
/// Where Tempo keeps its files. The normal build uses the user's application-data folder
/// (<c>%APPDATA%\Tempo</c> on Windows, <c>~/.config/Tempo</c> on Linux); the portable build keeps everything
/// in a <c>Tempo-data</c> folder next to the program, so it can run from a USB stick and leave nothing behind.
/// </summary>
public sealed record AppPaths(string DataDirectory, bool IsPortable)
{
    public const string AppName = "Tempo";

    public const string PortableFolderName = "Tempo-data";

    /// <summary>Folder used by releases made before the program was named Tempo.</summary>
    public const string LegacyFolderName = "StanfordController";

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string DefaultConfigurationPath => Path.Combine(DataDirectory, "Default" + ConfigurationStore.FileExtension);

    public static string UserDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static AppPaths ForUser() => new(UserDataDirectory, IsPortable: false);

    /// <summary>The portable data folder beside the program, or null if that location cannot be written.</summary>
    public static AppPaths? ForPortable(string programDirectory)
    {
        var directory = Path.Combine(programDirectory, PortableFolderName);
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return new AppPaths(directory, IsPortable: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies the instrument list saved by an earlier release (<c>StanfordController\instruments.json</c>)
    /// into the default configuration the first time this version runs. Returns true if anything was copied.
    /// </summary>
    public bool MigrateLegacyInstruments()
    {
        if (IsPortable || File.Exists(DefaultConfigurationPath))
        {
            return false;
        }

        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName, "instruments.json");
        if (!File.Exists(legacy))
        {
            return false;
        }

        try
        {
            var profiles = ConfigurationStore.Deserialize(File.ReadAllText(legacy));
            new ConfigurationStore(DefaultConfigurationPath).Save(profiles);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
