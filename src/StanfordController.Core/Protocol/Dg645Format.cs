using System.Globalization;

namespace StanfordController.Core.Protocol;

/// <summary>Number formatting and parsing for the DG645 wire protocol (always invariant culture).</summary>
public static class Dg645Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Standard "F" formats are exact for the requested number of decimals; custom formats such as
    // "0.000000000000" stop at 15 significant digits and would lose picoseconds above 1000 s.

    /// <summary>Seconds with 1 ps resolution, the same precision the instrument reports (e.g. "0.000001000000").</summary>
    public static string Time(double seconds) => seconds.ToString("F12", Inv);

    /// <summary>Hertz with 1 µHz resolution.</summary>
    public static string Frequency(double hertz) => TrimZeros(hertz.ToString("F6", Inv));

    /// <summary>Volts with 10 mV resolution.</summary>
    public static string Volts(double volts) => volts.ToString("F2", Inv);

    /// <summary>Removes trailing fractional zeros: "1.500" → "1.5", "2.000" → "2".</summary>
    public static string TrimZeros(string number) =>
        number.Contains('.') ? number.TrimEnd('0').TrimEnd('.') : number;

    public static string Integer(long value) => value.ToString(Inv);

    public static double ParseDouble(string text)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, Inv, out var value))
        {
            throw new FormatException($"Expected a number from the instrument but received '{text}'.");
        }

        return value;
    }

    public static long ParseLong(string text)
    {
        var trimmed = text.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, Inv, out var value))
        {
            return value;
        }

        // Some firmware replies with a float for integer quantities ("+1.0").
        if (double.TryParse(trimmed, NumberStyles.Float, Inv, out var d) && Math.Abs(d - Math.Round(d)) < 1e-9)
        {
            return (long)Math.Round(d);
        }

        throw new FormatException($"Expected an integer from the instrument but received '{text}'.");
    }

    public static int ParseInt(string text) => checked((int)ParseLong(text));

    public static bool ParseBool(string text) => ParseLong(text) != 0;
}
