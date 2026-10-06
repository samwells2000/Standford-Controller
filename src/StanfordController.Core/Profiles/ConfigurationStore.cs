using System.Text.Json;
using System.Text.Json.Serialization;

namespace StanfordController.Core.Profiles;

/// <summary>
/// A configuration file (<c>*.tempo</c>): the saved instruments, each with its connection details and,
/// once saved, its complete setup. Plain JSON so it can be archived, diffed or copied to another computer.
/// </summary>
public sealed class TempoConfiguration
{
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;

    public List<InstrumentProfile> Instruments { get; set; } = [];
}

/// <summary>Reads and writes one configuration file. Saves are atomic, so a crash never loses the previous file.</summary>
public sealed class ConfigurationStore(string path)
{
    public const string FileExtension = ".tempo";

    /// <summary>The extension used by release 1.0.14, when the program was called Strobe. The contents are the same.</summary>
    public const string PreviousFileExtension = ".strobe";

    public const string FileFilterName = "Tempo configuration";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = Path.GetFullPath(path);

    /// <summary>The file name without its extension, e.g. "Laser lab".</summary>
    public string Name => Path.GetFileNameWithoutExtension(FilePath);

    public bool Exists => File.Exists(FilePath);

    public IReadOnlyList<InstrumentProfile> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        return Deserialize(File.ReadAllText(FilePath));
    }

    public void Save(IEnumerable<InstrumentProfile> profiles)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, Serialize(profiles));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static string Serialize(IEnumerable<InstrumentProfile> profiles) =>
        JsonSerializer.Serialize(new TempoConfiguration { Instruments = profiles.ToList() }, JsonOptions);

    /// <summary>Reads a configuration, including version 1 files (instruments.json from earlier releases).</summary>
    public static IReadOnlyList<InstrumentProfile> Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<TempoConfiguration>(json, JsonOptions)
                   ?? throw new InvalidDataException("The file does not contain a Tempo configuration.");
        if (file.FormatVersion > TempoConfiguration.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"The file was written by a newer version of Tempo (format {file.FormatVersion}). Update Tempo to open it.");
        }

        return file.Instruments ?? [];
    }
}
