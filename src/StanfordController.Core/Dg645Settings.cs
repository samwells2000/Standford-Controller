using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StanfordController.Core.Protocol;
using StanfordController.Core.Units;

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
    public IReadOnlyList<string> DescribeDifferences(Dg645Settings other) => Compare(other).Select(c => c.Setting).ToList();

    /// <summary>
    /// The settings that differ between this setup (before) and <paramref name="other"/> (after), with both values
    /// written as the pages show them. Values within the instrument's resolution count as equal.
    /// </summary>
    public IReadOnlyList<SettingChange> Compare(Dg645Settings other)
    {
        var changes = new List<SettingChange>();
        void Check(bool same, string name, string before, string after)
        {
            if (!same)
            {
                changes.Add(new SettingChange(name, before, after));
            }
        }

        static bool SameTime(double a, double b) => Math.Abs(a - b) <= 0.5e-12 + 1e-12 * Math.Max(Math.Abs(a), Math.Abs(b));
        static bool SameFrequency(double a, double b) => Math.Abs(a - b) <= 0.5e-6 + 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b));
        static bool SameVolts(double a, double b) => Math.Abs(a - b) < 0.005;
        static string Time(double seconds) => EngineeringUnits.Format(seconds, Quantity.Time);
        static string Volts(double volts) => EngineeringUnits.Format(volts, Quantity.Voltage);
        static string OnOff(bool on) => on ? "On" : "Off";
        static string Item<T>(IReadOnlyList<T> list, int index) => index < list.Count ? Convert.ToString(list[index], CultureInfo.InvariantCulture) ?? "—" : "—";

        var (t, u) = (Trigger, other.Trigger);
        Check(t.Source == u.Source, "Trigger source", Dg645Names.Describe(t.Source), Dg645Names.Describe(u.Source));
        Check(SameFrequency(t.RateHz, u.RateHz), "Trigger rate",
            EngineeringUnits.Format(t.RateHz, Quantity.Frequency), EngineeringUnits.Format(u.RateHz, Quantity.Frequency));
        Check(SameVolts(t.LevelVolts, u.LevelVolts), "Trigger threshold", Volts(t.LevelVolts), Volts(u.LevelVolts));
        Check(SameTime(t.HoldoffSeconds, u.HoldoffSeconds), "Holdoff", Time(t.HoldoffSeconds), Time(u.HoldoffSeconds));
        Check(t.Inhibit == u.Inhibit, "Inhibit", Dg645Names.Describe(t.Inhibit), Dg645Names.Describe(u.Inhibit));
        Check(t.AdvancedTriggering == u.AdvancedTriggering, "Advanced triggering", OnOff(t.AdvancedTriggering), OnOff(u.AdvancedTriggering));
        for (var i = 0; i < Math.Max(t.PrescaleFactors.Count, u.PrescaleFactors.Count); i++)
        {
            var input = i == 0 ? "Trigger input" : ((PrescaleInput)i).ToString();
            Check(Item(t.PrescaleFactors, i) == Item(u.PrescaleFactors, i), $"{input} prescale factor", Item(t.PrescaleFactors, i), Item(u.PrescaleFactors, i));
        }

        for (var i = 0; i < Math.Max(t.PrescalePhases.Count, u.PrescalePhases.Count); i++)
        {
            var output = ((PrescaleInput)(i + 1)).ToString();
            Check(Item(t.PrescalePhases, i) == Item(u.PrescalePhases, i), $"{output} prescale phase", Item(t.PrescalePhases, i), Item(u.PrescalePhases, i));
        }

        var (b, c) = (Burst, other.Burst);
        Check(b.Enabled == c.Enabled, "Burst mode", OnOff(b.Enabled), OnOff(c.Enabled));
        Check(b.Count == c.Count, "Burst count", b.Count.ToString(CultureInfo.InvariantCulture), c.Count.ToString(CultureInfo.InvariantCulture));
        Check(SameTime(b.DelaySeconds, c.DelaySeconds), "Burst delay", Time(b.DelaySeconds), Time(c.DelaySeconds));
        Check(SameTime(b.PeriodSeconds, c.PeriodSeconds), "Burst period", Time(b.PeriodSeconds), Time(c.PeriodSeconds));
        Check(b.T0Mode == c.T0Mode, "Burst T0 output", Dg645Names.Describe(b.T0Mode), Dg645Names.Describe(c.T0Mode));

        foreach (var channel in Delays.Select(d => d.Channel).Union(other.Delays.Select(d => d.Channel)).Order())
        {
            var mine = Delays.FirstOrDefault(d => d.Channel == channel);
            var theirs = other.Delays.FirstOrDefault(d => d.Channel == channel);
            Check(mine is not null && theirs is not null && mine.Reference == theirs.Reference && SameTime(mine.DelaySeconds, theirs.DelaySeconds),
                $"Delay {channel}", DescribeDelay(mine), DescribeDelay(theirs));
        }

        foreach (var output in Outputs.Select(o => o.Output).Union(other.Outputs.Select(o => o.Output)).Order())
        {
            var mine = Outputs.FirstOrDefault(o => o.Output == output);
            var theirs = other.Outputs.FirstOrDefault(o => o.Output == output);
            if (mine is null || theirs is null)
            {
                changes.Add(new SettingChange($"{output} output", mine is null ? "—" : "Set", theirs is null ? "—" : "Set"));
                continue;
            }

            Check(SameVolts(mine.AmplitudeVolts, theirs.AmplitudeVolts), $"{output} amplitude", Volts(mine.AmplitudeVolts), Volts(theirs.AmplitudeVolts));
            Check(SameVolts(mine.OffsetVolts, theirs.OffsetVolts), $"{output} offset", Volts(mine.OffsetVolts), Volts(theirs.OffsetVolts));
            Check(mine.Polarity == theirs.Polarity, $"{output} polarity", Dg645Names.Describe(mine.Polarity), Dg645Names.Describe(theirs.Polarity));
        }

        return changes;
    }

    /// <summary>A channel's delay as the Delays page shows it, e.g. "A + 1.5 µs".</summary>
    public static string DescribeDelay(ChannelDelaySettings? delay) =>
        delay is null ? "—" : DescribeDelay(delay.Reference, delay.DelaySeconds);

    public static string DescribeDelay(Dg645Channel reference, double seconds) =>
        seconds < 0
            ? $"{reference} \u2212 {EngineeringUnits.Format(-seconds, Quantity.Time)}"
            : $"{reference} + {EngineeringUnits.Format(seconds, Quantity.Time)}";
}

/// <summary>One setting that differs between two setups, with both values as the pages show them.</summary>
public sealed record SettingChange(string Setting, string Before, string After);

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
