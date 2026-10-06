using System.Text.Json;

namespace StanfordController.Core.Profiles;

/// <summary>
/// Persists saved instruments as JSON, by default in
/// <c>%APPDATA%\StanfordController\instruments.json</c>.
/// </summary>
public sealed class ProfileStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "StanfordController");

    public static string DefaultPath { get; } = Path.Combine(DefaultDirectory, "instruments.json");

    public string FilePath { get; } = path;

    public IReadOnlyList<InstrumentProfile> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        return Deserialize(File.ReadAllText(FilePath));
    }

    /// <summary>Writes atomically so a crash mid-save never loses the existing list.</summary>
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
        JsonSerializer.Serialize(new ProfileFile { Instruments = profiles.ToList() }, JsonOptions);

    public static IReadOnlyList<InstrumentProfile> Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<ProfileFile>(json, JsonOptions)
                   ?? throw new InvalidDataException("The file does not contain an instrument list.");
        return file.Instruments;
    }

    private sealed class ProfileFile
    {
        public int FormatVersion { get; set; } = 1;

        public List<InstrumentProfile> Instruments { get; set; } = [];
    }
}
