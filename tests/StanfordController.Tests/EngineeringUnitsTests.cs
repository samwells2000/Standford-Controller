using System.Globalization;
using StanfordController.Core.Protocol;
using StanfordController.Core.Units;

namespace StanfordController.Tests;

public class EngineeringUnitsTests
{
    [Theory]
    [InlineData("1.5 us", 1.5e-6)]
    [InlineData("1.5µs", 1.5e-6)]
    [InlineData("250ns", 250e-9)]
    [InlineData("10 ps", 10e-12)]
    [InlineData("3 ms", 3e-3)]
    [InlineData("2 s", 2)]
    [InlineData("2", 2)]
    [InlineData("1e-6", 1e-6)]
    [InlineData("1E-6 s", 1e-6)]
    [InlineData("-5 ns", -5e-9)]
    [InlineData("0.5m", 0.5e-3)]
    [InlineData("1 sec", 1)]
    public void ParsesTime(string text, double expected)
    {
        Assert.True(EngineeringUnits.TryParse(text, Quantity.Time, out var value));
        Assert.Equal(expected, value, 1e-18);
    }

    [Theory]
    [InlineData("10 kHz", 10e3)]
    [InlineData("10k", 10e3)]
    [InlineData("1 MHz", 1e6)]
    [InlineData("1 mHz", 1e-3)]
    [InlineData("100 uHz", 100e-6)]
    [InlineData("1000", 1000)]
    public void ParsesFrequency(string text, double expected)
    {
        Assert.True(EngineeringUnits.TryParse(text, Quantity.Frequency, out var value));
        Assert.Equal(expected, value, 1e-12);
    }

    [Theory]
    [InlineData("-800 mV", -0.8)]
    [InlineData("4", 4)]
    [InlineData("2.5V", 2.5)]
    [InlineData("−0.8", -0.8)]
    public void ParsesVoltage(string text, double expected)
    {
        Assert.True(EngineeringUnits.TryParse(text, Quantity.Voltage, out var value));
        Assert.Equal(expected, value, 1e-12);
    }

    [Theory]
    [InlineData("", Quantity.Time)]
    [InlineData("abc", Quantity.Time)]
    [InlineData("5 Ms", Quantity.Time)]
    [InlineData("1.5", Quantity.Integer)]
    [InlineData("1 xs", Quantity.Time)]
    public void RejectsInvalidInput(string text, Quantity quantity)
    {
        Assert.False(EngineeringUnits.TryParse(text, quantity, out _));
    }

    [Theory]
    [InlineData(1.5e-6, Quantity.Time, "1.5 µs")]
    [InlineData(0, Quantity.Time, "0 s")]
    [InlineData(1.000000005e-3, Quantity.Time, "1.000000005 ms")]
    [InlineData(5e-12, Quantity.Time, "5 ps")]
    [InlineData(1999.999999999995, Quantity.Time, "1999.999999999995 s")]
    [InlineData(10e3, Quantity.Frequency, "10 kHz")]
    [InlineData(12345.678, Quantity.Frequency, "12.345678 kHz")]
    [InlineData(-0.8, Quantity.Voltage, "-0.80 V")]
    [InlineData(42, Quantity.Integer, "42")]
    [InlineData(5e-6 - 4e-6, Quantity.Time, "1 µs")]
    [InlineData(1e3 - 1e-9, Quantity.Frequency, "1 kHz")]
    public void FormatsWithPrefixes(double value, Quantity quantity, string expected)
    {
        Assert.Equal(expected, EngineeringUnits.Format(value, quantity));
    }

    [Theory]
    [InlineData(1e-6)]
    [InlineData(123.456e-9)]
    [InlineData(0.25)]
    [InlineData(1.234e-9)]
    [InlineData(1999.999999999995)]
    public void FormattedTimeParsesBackToTheSameValue(double seconds)
    {
        var text = EngineeringUnits.Format(seconds, Quantity.Time);
        Assert.True(EngineeringUnits.TryParse(text, Quantity.Time, out var parsed));
        Assert.Equal(seconds, parsed, Math.Max(1e-15, seconds * 1e-15));
    }

    [Fact]
    public void FormattingRoundsToOnePicosecond()
    {
        Assert.Equal("123.457 ns", EngineeringUnits.Format(123.456789e-9, Quantity.Time));
    }

    [Fact]
    public void WireFormatKeepsPicosecondsAtTheTopOfTheRange()
    {
        Assert.Equal("1999.999999999995", Dg645Format.Time(1999.999999999995));
        Assert.Equal("10000000", Dg645Format.Frequency(10e6));
        Assert.Equal("0.0001", Dg645Format.Frequency(100e-6));
    }

    [Fact]
    public void WireFormatIgnoresTheCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("0.000001500000", Dg645Format.Time(1.5e-6));
            Assert.Equal("-0.80", Dg645Format.Volts(-0.8));
            Assert.Equal(1.5, Dg645Format.ParseDouble("+1.500"));
            Assert.True(EngineeringUnits.TryParse("1.5 us", Quantity.Time, out var value));
            Assert.Equal(1.5e-6, value, 1e-18);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
