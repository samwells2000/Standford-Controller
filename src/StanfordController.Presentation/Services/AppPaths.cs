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

    /// <summary>The program's name in release 1.0.14, which is also the name of that release's data folder.</summary>
    public const string PreviousAppName = "Strobe";

    /// <summary>The portable data folder of release 1.0.14.</summary>
    public const string PreviousPortableFolderName = "Strobe-data";

    /// <summary>Folder used by releases made before the program was named Strobe.</summary>
    public const string LegacyFolderName = "StanfordController";

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string DefaultConfigurationPath => Path.Combine(DataDirectory, "Default" + ConfigurationStore.FileExtension);

    /// <summary>
    /// Where release 1.0.14 (Strobe) kept the same files: <c>Strobe</c> beside <c>Tempo</c> in the
    /// application-data folder, or <c>Strobe-data</c> beside <c>Tempo-data</c> for the portable build.
    /// </summary>
    public string PreviousDataDirectory =>
        Path.Combine(Path.GetDirectoryName(DataDirectory) ?? string.Empty, IsPortable ? PreviousPortableFolderName : PreviousAppName);

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
    /// The first time this version runs, copies the configurations and preferences left by release 1.0.14 (Strobe):
    /// each <c>*.strobe</c> file in its data folder becomes a <c>*.tempo</c> file here, and the preferences are
    /// pointed at the copies. The old folder is left as it was. Returns true if anything was copied.
    /// </summary>
    public bool MigrateFromStrobe()
    {
        var previous = PreviousDataDirectory;
        if (File.Exists(SettingsPath) || File.Exists(DefaultConfigurationPath) || !Directory.Exists(previous))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(DataDirectory);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var copies = new Dictionary<string, string>(comparer);
            foreach (var file in Directory.EnumerateFiles(previous, "*" + ConfigurationStore.PreviousFileExtension))
            {
                var copy = Path.Combine(DataDirectory, Path.GetFileNameWithoutExtension(file) + ConfigurationStore.FileExtension);
                if (!File.Exists(copy))
                {
                    File.Copy(file, copy);
                }

                copies[Path.GetFullPath(file)] = copy;
            }

            var previousSettings = Path.Combine(previous, "settings.json");
            if (File.Exists(previousSettings))
            {
                // Configurations saved elsewhere keep their .strobe names; they open as they are.
                string Moved(string path) => copies.TryGetValue(Path.GetFullPath(path), out var copy) ? copy : path;
                var settings = new AppSettingsStore(previousSettings).Load();
                settings.ConfigurationPath = settings.ConfigurationPath is { } current ? Moved(current) : null;
                settings.RecentConfigurations = settings.RecentConfigurations.Select(Moved).ToList();
                new AppSettingsStore(SettingsPath).Save(settings);
            }

            return copies.Count > 0 || File.Exists(previousSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
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
