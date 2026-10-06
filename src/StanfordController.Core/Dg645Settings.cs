using System.Text.Json;
using System.Text.Json.Serialization;
using StanfordController.Core.Protocol;

namespace StanfordController.Core;

/// <summary>
/// A complete, portable snapshot of a DG645's delay, output, trigger and burst configuration.
/// Saved to <c>*.dg645.json</c> files so a setup can be archived, shared or applied to another unit.
/// </summary>
public sealed class Dg645Settings
{
    public const string FileExtension = ".dg645.json";

    public int FormatVersion { get; set; } = 1;

    public string? Description { get; set; }

    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;

    public string? SourceIdentity { get; set; }

    public TriggerSettings Trigger { get; set; } = new();

    public BurstSettings Burst { get; set; } = new();

    public List<ChannelDelaySettings> Delays { get; set; } = [];

    public List<OutputLevelSettings> Outputs { get; set; } = [];

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static Dg645Settings FromJson(string json) =>
        JsonSerializer.Deserialize<Dg645Settings>(json, JsonOptions)
        ?? throw new InvalidDataException("The file does not contain DG645 settings.");

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default) =>
        await File.WriteAllTextAsync(path, ToJson(), cancellationToken).ConfigureAwait(false);

    public static async Task<Dg645Settings> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        FromJson(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Names the settings that differ between this setup and <paramref name="other"/>, e.g. "Delay C" or
    /// "AB amplitude". Values within the instrument's resolution count as equal. Empty when the setups match.
    /// </summary>
    public IReadOnlyList<string> DescribeDifferences(Dg645Settings other)
    {
        var differences = new List<string>();
        void Check(bool same, string name)
        {
            if (!same)
            {
                differences.Add(name);
            }
        }

        static bool SameTime(double a, double b) => Math.Abs(a - b) <= 0.5e-12 + 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b));
        static bool SameFrequency(double a, double b) => Math.Abs(a - b) <= 0.5e-6 + 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b));
        static bool SameVolts(double a, double b) => Math.Abs(a - b) < 0.005;

        Check(Trigger.Source == other.Trigger.Source, "Trigger source");
        Check(SameFrequency(Trigger.RateHz, other.Trigger.RateHz), "Trigger rate");
        Check(SameVolts(Trigger.LevelVolts, other.Trigger.LevelVolts), "Trigger threshold");
        Check(SameTime(Trigger.HoldoffSeconds, other.Trigger.HoldoffSeconds), "Holdoff");
        Check(Trigger.Inhibit == other.Trigger.Inhibit, "Inhibit");
        Check(Trigger.AdvancedTriggering == other.Trigger.AdvancedTriggering, "Advanced triggering");
        Check(Trigger.PrescaleFactors.SequenceEqual(other.Trigger.PrescaleFactors), "Prescale factors");
        Check(Trigger.PrescalePhases.SequenceEqual(other.Trigger.PrescalePhases), "Prescale phases");

        Check(Burst.Enabled == other.Burst.Enabled, "Burst mode");
        Check(Burst.Count == other.Burst.Count, "Burst count");
        Check(SameTime(Burst.DelaySeconds, other.Burst.DelaySeconds), "Burst delay");
        Check(SameTime(Burst.PeriodSeconds, other.Burst.PeriodSeconds), "Burst period");
        Check(Burst.T0Mode == other.Burst.T0Mode, "Burst T0 output");

        foreach (var channel in Delays.Select(d => d.Channel).Union(other.Delays.Select(d => d.Channel)).Order())
        {
            var mine = Delays.FirstOrDefault(d => d.Channel == channel);
            var theirs = other.Delays.FirstOrDefault(d => d.Channel == channel);
            Check(mine is not null && theirs is not null && mine.Reference == theirs.Reference && SameTime(mine.DelaySeconds, theirs.DelaySeconds),
                $"Delay {channel}");
        }

        foreach (var output in Outputs.Select(o => o.Output).Union(other.Outputs.Select(o => o.Output)).Order())
        {
            var mine = Outputs.FirstOrDefault(o => o.Output == output);
            var theirs = other.Outputs.FirstOrDefault(o => o.Output == output);
            if (mine is null || theirs is null)
            {
                differences.Add($"{output} output");
                continue;
            }

            Check(SameVolts(mine.AmplitudeVolts, theirs.AmplitudeVolts), $"{output} amplitude");
            Check(SameVolts(mine.OffsetVolts, theirs.OffsetVolts), $"{output} offset");
            Check(mine.Polarity == theirs.Polarity, $"{output} polarity");
        }

        return differences;
    }
}

public sealed class TriggerSettings
{
    public TriggerSource Source { get; set; } = TriggerSource.Internal;

    public double RateHz { get; set; } = 10_000;

    public double LevelVolts { get; set; } = 1.0;

    public double HoldoffSeconds { get; set; }

    public InhibitMode Inhibit { get; set; } = InhibitMode.Off;

    public bool AdvancedTriggering { get; set; }

    /// <summary>PRES factors indexed by <see cref="PrescaleInput"/> (trigger, AB, CD, EF, GH).</summary>
    public List<long> PrescaleFactors { get; set; } = [1, 1, 1, 1, 1];

    /// <summary>PHAS phases for AB, CD, EF, GH (PHAS index 1-4).</summary>
    public List<long> PrescalePhases { get; set; } = [0, 0, 0, 0];
}

public sealed class BurstSettings
{
    public bool Enabled { get; set; }

    public long Count { get; set; } = 10;

    public double DelaySeconds { get; set; }

    public double PeriodSeconds { get; set; } = 1e-6;

    public BurstT0Mode T0Mode { get; set; } = BurstT0Mode.EveryDelayCycle;
}

public sealed class ChannelDelaySettings
{
    public Dg645Channel Channel { get; set; }

    public Dg645Channel Reference { get; set; } = Dg645Channel.T0;

    public double DelaySeconds { get; set; }
}

public sealed class OutputLevelSettings
{
    public Dg645Output Output { get; set; }

    public double AmplitudeVolts { get; set; } = 2.5;

    public double OffsetVolts { get; set; }

    public OutputPolarity Polarity { get; set; } = OutputPolarity.Positive;
}
