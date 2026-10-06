using System.Globalization;
using StanfordController.Core.Protocol;

namespace StanfordController.Core.Units;

/// <summary>The physical quantity a numeric field holds, which decides its unit and SI prefixes.</summary>
public enum Quantity
{
    /// <summary>Seconds. Accepts ps, ns, us/µs, ms, s.</summary>
    Time,

    /// <summary>Hertz. Accepts uHz, mHz, Hz, kHz, MHz.</summary>
    Frequency,

    /// <summary>Volts. Accepts mV and V.</summary>
    Voltage,

    /// <summary>A plain whole number (counts, prescale factors, addresses).</summary>
    Integer,
}

/// <summary>
/// Parses and formats values with SI prefixes ("1.5 us", "10kHz", "-800 mV") using the
/// invariant culture, so a comma decimal separator in the Windows locale never reaches the instrument.
/// </summary>
public static class EngineeringUnits
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string UnitSymbol(Quantity quantity) => quantity switch
    {
        Quantity.Time => "s",
        Quantity.Frequency => "Hz",
        Quantity.Voltage => "V",
        _ => "",
    };

    public static bool TryParse(string? text, Quantity quantity, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().Replace(" ", string.Empty).Replace(" ", string.Empty).Replace('−', '-');

        if (quantity == Quantity.Integer)
        {
            if (!double.TryParse(s, NumberStyles.Float, Inv, out value) || double.IsNaN(value) || double.IsInfinity(value))
            {
                return false;
            }

            return Math.Abs(value - Math.Round(value)) < 1e-9;
        }

        var unit = UnitSymbol(quantity);
        if (s.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^unit.Length];
        }
        else if (quantity == Quantity.Time && s.EndsWith("sec", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^3];
        }

        double multiplier = 1;
        if (s.Length > 0 && !char.IsDigit(s[^1]) && s[^1] != '.')
        {
            if (!TryGetPrefix(s[^1], quantity, out multiplier))
            {
                return false;
            }

            s = s[..^1];
        }

        if (!double.TryParse(s, NumberStyles.Float, Inv, out var mantissa) || double.IsNaN(mantissa) || double.IsInfinity(mantissa))
        {
            return false;
        }

        value = mantissa * multiplier;
        return true;
    }

    private static bool TryGetPrefix(char c, Quantity quantity, out double multiplier)
    {
        multiplier = c switch
        {
            'p' => 1e-12,
            'n' => 1e-9,
            'u' or 'µ' or 'μ' => 1e-6,
            'm' => 1e-3,
            'k' or 'K' => 1e3,
            'M' => 1e6,
            'G' => 1e9,
            _ => 0,
        };

        if (multiplier == 0)
        {
            return false;
        }

        // Reject prefixes that make no sense for the quantity (e.g. "5 Gs" or "3 ps" of voltage).
        return quantity switch
        {
            Quantity.Time => multiplier <= 1e3,
            Quantity.Frequency => multiplier >= 1e-6,
            Quantity.Voltage => multiplier is >= 1e-3 and <= 1e3,
            _ => true,
        };
    }

    /// <summary>Formats with the most readable SI prefix, e.g. 1.5e-6 s → "1.5 µs".</summary>
    public static string Format(double value, Quantity quantity)
    {
        if (double.IsNaN(value))
        {
            return "—";
        }

        switch (quantity)
        {
            case Quantity.Integer:
                return Math.Round(value).ToString("0", Inv);
            case Quantity.Voltage:
                return value.ToString("F2", Inv) + " V";
        }

        var unit = UnitSymbol(quantity);
        if (value == 0)
        {
            return "0 " + unit;
        }

        var (scale, prefix) = quantity == Quantity.Time ? PickTimePrefix(Math.Abs(value)) : PickFrequencyPrefix(Math.Abs(value));

        // Time is resolved to 1 ps and frequency to 1 µHz, matching the instrument's readback precision.
        var resolution = quantity == Quantity.Time ? 1e-12 : 1e-6;
        var decimals = Math.Clamp((int)Math.Round(Math.Log10(scale / resolution)), 0, 15);
        var mantissa = Dg645Format.TrimZeros((value / scale).ToString("F" + decimals, Inv));
        return (mantissa == "-0" ? "0" : mantissa) + " " + prefix + unit;
    }

    private static (double Scale, string Prefix) PickTimePrefix(double magnitude) => magnitude switch
    {
        >= 1 => (1, ""),
        >= 1e-3 => (1e-3, "m"),
        >= 1e-6 => (1e-6, "µ"),
        >= 1e-9 => (1e-9, "n"),
        _ => (1e-12, "p"),
    };

    private static (double Scale, string Prefix) PickFrequencyPrefix(double magnitude) => magnitude switch
    {
        >= 1e6 => (1e6, "M"),
        >= 1e3 => (1e3, "k"),
        >= 1 => (1, ""),
        >= 1e-3 => (1e-3, "m"),
        _ => (1e-6, "µ"),
    };
}
