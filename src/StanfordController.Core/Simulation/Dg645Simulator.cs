using System.Globalization;
using StanfordController.Core.Protocol;

namespace StanfordController.Core.Simulation;

/// <summary>
/// A software model of a DG645's remote interface. It answers the full command set with plausible
/// values and validation so the application can be explored, demonstrated and tested without hardware.
/// </summary>
public sealed class Dg645Simulator
{
    private const int MaxErrors = 20;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly object _sync = new();
    private readonly Queue<int> _errors = new();
    private readonly State?[] _savedSetups = new State?[10];
    private State _state = new();
    private int _insr;
    private bool _armed;
    private int _esr = (int)StandardEventFlags.PowerOn;
    private int _inse;
    private int _ese;
    private int _sre;
    private bool _psc = true;
    private int _displayMode = (int)DisplayMode.ChannelDelay;
    private int _displayChannel = (int)Dg645Channel.A;
    private bool _locked;
    private readonly string[] _interface =
    [
        "1", "9600", "1", "15", "1", "1", "1", "0", "1", "1", "1", "0.0.0.0", "0.0.0.0", "0.0.0.0",
    ];

    public string Identity { get; set; } = "Stanford Research Systems,DG645,s/n000000,ver1.14.10E";

    public string MacAddress { get; set; } = "00:19:b3:0a:06:45";

    public Timebase Timebase { get; set; } = Timebase.Internal;

    /// <summary>
    /// False makes the instrument ignore everything sent to it and never reply, as if it had hung or its cable had
    /// been pulled, for testing the connection heartbeat.
    /// </summary>
    public bool IsResponding { get; set; } = true;

    /// <summary>Sets bits in the instrument status register, e.g. PLL_UNLOCK, as the hardware would on a fault.</summary>
    public void RaiseStatus(InstrumentStatusFlags flags)
    {
        lock (_sync)
        {
            _insr |= (int)flags;
        }
    }

    /// <summary>True after *TRG has armed a single-shot external trigger mode, until an external edge arrives.</summary>
    public bool IsArmed
    {
        get
        {
            lock (_sync)
            {
                return _armed;
            }
        }
    }

    /// <summary>Every line received, for tests. Use <see cref="GetReceivedLines"/> from other threads.</summary>
    public List<string> ReceivedLines { get; } = [];

    public IReadOnlyList<string> GetReceivedLines()
    {
        lock (_sync)
        {
            return ReceivedLines.ToArray();
        }
    }

    /// <summary>
    /// An edge at the rear-panel trigger input, for tests and demonstrations. It starts a delay cycle when the
    /// source is external, or when a single-shot external mode has been armed (which it then disarms).
    /// </summary>
    public void ExternalTrigger()
    {
        lock (_sync)
        {
            var source = (TriggerSource)_state.TriggerSource;
            if (source is TriggerSource.ExternalRising or TriggerSource.ExternalFalling
                || (_armed && source is TriggerSource.SingleShotExternalRising or TriggerSource.SingleShotExternalFalling))
            {
                _armed = false;
                RunDelayCycle();
            }
        }
    }

    /// <summary>Processes one line (which may hold several ';'-separated commands) and returns the reply lines.</summary>
    public IReadOnlyList<string> Process(string line)
    {
        lock (_sync)
        {
            ReceivedLines.Add(line);
            var replies = new List<string>();
            if (!IsResponding)
            {
                return replies;
            }

            foreach (var raw in line.Split(';'))
            {
                var command = raw.Trim();
                if (command.Length == 0)
                {
                    continue;
                }

                try
                {
                    var reply = Execute(command);
                    if (reply is not null)
                    {
                        replies.Add(reply);
                    }
                }
                catch (SimulatedError error)
                {
                    PushError(error.Code);
                }
            }

            return replies;
        }
    }

    private string? Execute(string command)
    {
        // Mnemonic: optional '*', then letters. Then optional '?', then comma-separated parameters.
        var i = command.StartsWith('*') ? 1 : 0;
        while (i < command.Length && char.IsLetter(command[i]))
        {
            i++;
        }

        var mnemonic = command[..i].ToUpperInvariant();
        var rest = command[i..].TrimStart();
        var isQuery = rest.StartsWith('?');
        if (isQuery)
        {
            rest = rest[1..];
        }

        var args = rest.Trim().Length == 0
            ? []
            : rest.Split(',').Select(a => a.Trim()).ToArray();

        var s = _state;
        switch (mnemonic)
        {
            // ---- IEEE-488.2 common commands ----
            case "*IDN": RequireQuery(isQuery); return Identity;
            case "*RST": RequireSet(isQuery); _state = new State(); _armed = false; return null;
            case "*CLS": RequireSet(isQuery); _insr = 0; _esr = 0; _errors.Clear(); return null;
            case "*TRG": RequireSet(isQuery); FireTrigger(); return null;
            case "*WAI": RequireSet(isQuery); return null;
            case "*OPC":
                if (isQuery)
                {
                    return "1";
                }

                _esr |= (int)StandardEventFlags.OperationComplete;
                return null;
            case "*TST": RequireQuery(isQuery); return "0";
            case "*CAL": RequireQuery(isQuery); return "0";
            case "*SAV":
                RequireSet(isQuery);
                _savedSetups[Int(args, 0, 1, 9)] = s.Clone();
                return null;
            case "*RCL":
                {
                    RequireSet(isQuery);
                    var slot = Int(args, 0, 0, 9);
                    _armed = false;
                    if (slot == 0)
                    {
                        _state = new State();
                    }
                    else
                    {
                        _state = _savedSetups[slot]?.Clone() ?? throw new SimulatedError(14);
                    }

                    return null;
                }

            case "*ESR": RequireQuery(isQuery); return ReadAndClear(ref _esr);
            case "*ESE": return Register(ref _ese, isQuery, args);
            case "*SRE": return Register(ref _sre, isQuery, args);
            case "*STB": RequireQuery(isQuery); return StatusByte().ToString(Inv);
            case "*PSC":
                if (isQuery)
                {
                    return _psc ? "1" : "0";
                }

                _psc = Int(args, 0, 0, 1) == 1;
                return null;

            // ---- Status and display ----
            case "INSR": RequireQuery(isQuery); AddBackgroundActivity(); return ReadAndClear(ref _insr);
            case "INSE": return Register(ref _inse, isQuery, args);
            case "LERR":
                RequireQuery(isQuery);
                return (_errors.Count > 0 ? _errors.Dequeue() : 0).ToString(Inv);
            case "TIMB": RequireQuery(isQuery); return ((int)Timebase).ToString(Inv);
            case "DISP":
                if (isQuery)
                {
                    return $"{_displayMode},{_displayChannel}";
                }

                RequireArgs(args, 2);
                _displayMode = Int(args, 0, 0, 14);
                _displayChannel = Int(args, 1, 0, 9);
                return null;

            // ---- Trigger ----
            case "TSRC":
                {
                    var reply = IntSetting(ref s.TriggerSource, isQuery, args, 0, 6);
                    if (!isQuery)
                    {
                        // Choosing a trigger source, even the same one, cancels an armed single shot.
                        _armed = false;
                    }

                    return reply;
                }
            case "TRAT": return DoubleSetting(ref s.TriggerRate, isQuery, args, 100e-6, 10e6, "0.000000");
            case "TLVL": return DoubleSetting(ref s.TriggerLevel, isQuery, args, -3.5, 3.5, "0.00");
            case "HOLD": return DoubleSetting(ref s.Holdoff, isQuery, args, 0, 2000, "0.000000000000");
            case "INHB": return IntSetting(ref s.Inhibit, isQuery, args, 0, 5);
            case "ADVT": return IntSetting(ref s.AdvancedTriggering, isQuery, args, 0, 1);
            case "PRES":
                {
                    var index = Int(args, 0, 0, 4);
                    if (isQuery)
                    {
                        return s.Prescale[index].ToString(Inv);
                    }

                    RequireArgs(args, 2);
                    s.Prescale[index] = Long(args, 1, 1, (1L << 30) - 1);
                    if (index > 0 && s.Phase[index] >= s.Prescale[index])
                    {
                        s.Phase[index] = 0;
                    }

                    return null;
                }

            case "PHAS":
                {
                    var index = Int(args, 0, 1, 4);
                    if (isQuery)
                    {
                        return s.Phase[index].ToString(Inv);
                    }

                    RequireArgs(args, 2);
                    s.Phase[index] = Long(args, 1, 0, s.Prescale[index] - 1);
                    return null;
                }

            // ---- Burst ----
            case "BURM": return IntSetting(ref s.BurstMode, isQuery, args, 0, 1);
            case "BURC":
                if (isQuery)
                {
                    return s.BurstCount.ToString(Inv);
                }

                s.BurstCount = Long(args, 0, 1, uint.MaxValue);
                return null;
            case "BURD": return DoubleSetting(ref s.BurstDelay, isQuery, args, 0, 2000, "0.000000000000");
            case "BURP": return DoubleSetting(ref s.BurstPeriod, isQuery, args, 100e-9, 2000 - 10e-9, "0.000000000000");
            case "BURT": return IntSetting(ref s.BurstT0, isQuery, args, 0, 1);

            // ---- Delays ----
            case "DLAY":
                {
                    var channel = Int(args, 0, 0, 9);
                    if (isQuery)
                    {
                        return $"{s.Reference[channel]},{FormatSigned(s.Delay[channel], "0.000000000000")}";
                    }

                    RequireArgs(args, 3);
                    if (channel < 2)
                    {
                        throw new SimulatedError(10);
                    }

                    var reference = Int(args, 1, 0, 9);
                    var seconds = Double(args, 2, -2000, 2000);
                    SetDelay(channel, reference, seconds);
                    return null;
                }

            case "LINK":
                {
                    var channel = Int(args, 0, 0, 9);
                    if (isQuery)
                    {
                        return s.Reference[channel].ToString(Inv);
                    }

                    RequireArgs(args, 2);
                    if (channel < 2)
                    {
                        throw new SimulatedError(10);
                    }

                    SetDelay(channel, Int(args, 1, 0, 9), s.Delay[channel]);
                    return null;
                }

            case "SSDL":
                {
                    var channel = Int(args, 0, 0, 9);
                    if (isQuery)
                    {
                        return FormatSigned(s.DelayStep[channel], "0.000000000000");
                    }

                    RequireArgs(args, 2);
                    s.DelayStep[channel] = Double(args, 1, 0, 2000);
                    return null;
                }

            case "SPDL":
                {
                    RequireSet(isQuery);
                    RequireArgs(args, 2);
                    var channel = Int(args, 0, 2, 9);
                    var up = Int(args, 1, 0, 1) == 1;
                    SetDelay(channel, s.Reference[channel], s.Delay[channel] + (up ? 1 : -1) * s.DelayStep[channel]);
                    return null;
                }

            // ---- Output levels ----
            case "LAMP": return OutputDouble(s.Amplitude, isQuery, args, 0.5, 5.0, checkLevels: true);
            case "LOFF": return OutputDouble(s.Offset, isQuery, args, -2.0, 2.0, checkLevels: true);
            case "LPOL":
                {
                    var output = Int(args, 0, 0, 4);
                    if (isQuery)
                    {
                        return s.Polarity[output].ToString(Inv);
                    }

                    RequireArgs(args, 2);
                    s.Polarity[output] = Int(args, 1, 0, 1);
                    return null;
                }

            case "SSLA": return OutputDouble(s.AmplitudeStep, isQuery, args, 0, 5.0, checkLevels: false);
            case "SSLO": return OutputDouble(s.OffsetStep, isQuery, args, 0, 4.0, checkLevels: false);
            case "SPLA":
                {
                    RequireSet(isQuery);
                    RequireArgs(args, 2);
                    var output = Int(args, 0, 0, 4);
                    var next = s.Amplitude[output] + (Int(args, 1, 0, 1) == 1 ? 1 : -1) * s.AmplitudeStep[output];
                    SetLevel(s.Amplitude, output, next, 0.5, 5.0);
                    return null;
                }

            case "SPLO":
                {
                    RequireSet(isQuery);
                    RequireArgs(args, 2);
                    var output = Int(args, 0, 0, 4);
                    var next = s.Offset[output] + (Int(args, 1, 0, 1) == 1 ? 1 : -1) * s.OffsetStep[output];
                    SetLevel(s.Offset, output, next, -2.0, 2.0);
                    return null;
                }

            // ---- Interfaces ----
            case "IFCF":
                {
                    var index = Int(args, 0, 0, 13);
                    if (isQuery)
                    {
                        return _interface[index];
                    }

                    RequireArgs(args, 2);
                    _interface[index] = ValidateInterfaceValue(index, args[1]);
                    return null;
                }

            case "IFRS": RequireSet(isQuery); Int(args, 0, 0, 2); return null;
            case "EMAC": RequireQuery(isQuery); return MacAddress;
            case "LCAL": RequireSet(isQuery); return null;
            case "REMT": RequireSet(isQuery); return null;
            case "LOCK":
                RequireQuery(isQuery);
                if (_locked)
                {
                    return "0";
                }

                _locked = true;
                return "1";
            case "UNLK":
                RequireQuery(isQuery);
                var wasLocked = _locked;
                _locked = false;
                return wasLocked ? "1" : "0";

            default:
                throw new SimulatedError(111);
        }
    }

    // ------------------------------------------------------------------------------------------

    private void SetDelay(int channel, int reference, double seconds)
    {
        var s = _state;
        if (reference == channel || reference == 1)
        {
            throw new SimulatedError(13);
        }

        var oldReference = s.Reference[channel];
        var oldDelay = s.Delay[channel];
        s.Reference[channel] = reference;
        s.Delay[channel] = Math.Round(seconds / 5e-12) * 5e-12; // 5 ps resolution

        if (!TryGetAbsolute(channel, out _))
        {
            s.Reference[channel] = oldReference;
            s.Delay[channel] = oldDelay;
            throw new SimulatedError(13);
        }

        for (var c = 2; c <= 9; c++)
        {
            if (TryGetAbsolute(c, out var absolute) && absolute is < -1e-15 or > 2000 + 1e-12)
            {
                s.Reference[channel] = oldReference;
                s.Delay[channel] = oldDelay;
                throw new SimulatedError(12);
            }
        }
    }

    /// <summary>Absolute delay of a channel from T0; false if its references form a loop.</summary>
    public bool TryGetAbsolute(int channel, out double seconds)
    {
        seconds = 0;
        var visited = new HashSet<int>();
        while (channel > 1)
        {
            if (!visited.Add(channel))
            {
                return false;
            }

            seconds += _state.Delay[channel];
            channel = _state.Reference[channel];
        }

        return true;
    }

    private string? OutputDouble(double[] values, bool isQuery, string[] args, double min, double max, bool checkLevels)
    {
        var output = Int(args, 0, 0, 4);
        if (isQuery)
        {
            return FormatSigned(values[output], "0.00");
        }

        RequireArgs(args, 2);
        var value = Double(args, 1, checkLevels ? -10 : min, checkLevels ? 10 : max);
        if (checkLevels)
        {
            SetLevel(values, output, value, min, max);
        }
        else
        {
            values[output] = value;
        }

        return null;
    }

    private void SetLevel(double[] values, int output, double value, double min, double max)
    {
        value = Math.Round(value, 2);
        if (value < min - 1e-9 || value > max + 1e-9)
        {
            throw new SimulatedError(10);
        }

        var old = values[output];
        values[output] = value;

        // Combined swing must stay within the output stage's range.
        var s = _state;
        if (Math.Abs(s.Offset[output]) + s.Amplitude[output] > 6.0 + 1e-9)
        {
            values[output] = old;
            throw new SimulatedError(10);
        }
    }

    /// <summary>*TRG: one delay cycle in single-shot mode; in the single-shot external modes it arms the instrument.</summary>
    private void FireTrigger()
    {
        switch ((TriggerSource)_state.TriggerSource)
        {
            case TriggerSource.SingleShot:
                RunDelayCycle();
                break;
            case TriggerSource.SingleShotExternalRising or TriggerSource.SingleShotExternalFalling:
                _armed = true;
                break;
            default:
                throw new SimulatedError(11);
        }
    }

    private void RunDelayCycle()
    {
        _insr |= (int)(InstrumentStatusFlags.Trigger | InstrumentStatusFlags.EndOfDelay);
        if (_state.BurstMode == 1)
        {
            _insr |= (int)InstrumentStatusFlags.EndOfBurst;
        }
    }

    /// <summary>Internal and line triggering run continuously, so the status register keeps filling.</summary>
    private void AddBackgroundActivity()
    {
        if ((TriggerSource)_state.TriggerSource is TriggerSource.Internal or TriggerSource.Line
            && _state.Inhibit != (int)InhibitMode.Triggers)
        {
            _insr |= (int)(InstrumentStatusFlags.Trigger | InstrumentStatusFlags.EndOfDelay);
            if (_state.BurstMode == 1)
            {
                _insr |= (int)InstrumentStatusFlags.EndOfBurst;
            }
        }
    }

    private int StatusByte()
    {
        var stb = 0;
        if ((_insr & _inse) != 0)
        {
            stb |= (int)StatusByteFlags.InstrumentStatusSummary;
        }

        if ((_esr & _ese) != 0)
        {
            stb |= (int)StatusByteFlags.EventStatusSummary;
        }

        if ((stb & _sre) != 0)
        {
            stb |= (int)StatusByteFlags.MasterSummaryStatus;
        }

        return stb;
    }

    private void PushError(int code)
    {
        if (_errors.Count >= MaxErrors)
        {
            return;
        }

        _errors.Enqueue(_errors.Count == MaxErrors - 1 ? 254 : code);
        _esr |= code switch
        {
            >= 110 and < 170 => (int)StandardEventFlags.CommandError,
            >= 10 and < 30 => (int)StandardEventFlags.ExecutionError,
            _ => (int)StandardEventFlags.DeviceDependentError,
        };
    }

    private static string ValidateInterfaceValue(int index, string value)
    {
        switch ((InterfaceSetting)index)
        {
            case InterfaceSetting.Rs232BaudRate:
                if (!int.TryParse(value, NumberStyles.Integer, Inv, out var baud) || !Dg645Names.BaudRates.Contains(baud))
                {
                    throw new SimulatedError(10);
                }

                return baud.ToString(Inv);
            case InterfaceSetting.GpibAddress:
                return ParseRange(value, 0, 30).ToString(Inv);
            case InterfaceSetting.StaticIpAddress or InterfaceSetting.SubnetMask or InterfaceSetting.DefaultGateway:
                if (!System.Net.IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    throw new SimulatedError(10);
                }

                return ip.ToString();
            default:
                return ParseRange(value, 0, 1).ToString(Inv);
        }
    }

    private static int ParseRange(string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.Integer, Inv, out var number))
        {
            throw new SimulatedError(120);
        }

        if (number < min || number > max)
        {
            throw new SimulatedError(10);
        }

        return number;
    }

    private static string ReadAndClear(ref int register)
    {
        var value = register;
        register = 0;
        return value.ToString(Inv);
    }

    private static string? Register(ref int register, bool isQuery, string[] args)
    {
        if (isQuery)
        {
            return register.ToString(Inv);
        }

        register = Int(args, 0, 0, 255);
        return null;
    }

    private static string? IntSetting(ref int field, bool isQuery, string[] args, int min, int max)
    {
        if (isQuery)
        {
            return field.ToString(Inv);
        }

        field = Int(args, 0, min, max);
        return null;
    }

    private static string? DoubleSetting(ref double field, bool isQuery, string[] args, double min, double max, string format)
    {
        if (isQuery)
        {
            return FormatSigned(field, format);
        }

        field = Double(args, 0, min, max);
        return null;
    }

    private static string FormatSigned(double value, string format) =>
        (value >= 0 ? "+" : string.Empty) + value.ToString(format, Inv);

    private static void RequireQuery(bool isQuery)
    {
        if (!isQuery)
        {
            throw new SimulatedError(113);
        }
    }

    private static void RequireSet(bool isQuery)
    {
        if (isQuery)
        {
            throw new SimulatedError(112);
        }
    }

    private static void RequireArgs(string[] args, int count)
    {
        if (args.Length < count)
        {
            throw new SimulatedError(116);
        }

        if (args.Length > count)
        {
            throw new SimulatedError(115);
        }
    }

    private static int Int(string[] args, int index, int min, int max) => checked((int)Long(args, index, min, max));

    private static long Long(string[] args, int index, long min, long max)
    {
        if (args.Length <= index || args[index].Length == 0)
        {
            throw new SimulatedError(116);
        }

        if (!long.TryParse(args[index], NumberStyles.Integer, Inv, out var value))
        {
            throw new SimulatedError(120);
        }

        if (value < min || value > max)
        {
            throw new SimulatedError(10);
        }

        return value;
    }

    private static double Double(string[] args, int index, double min, double max)
    {
        if (args.Length <= index || args[index].Length == 0)
        {
            throw new SimulatedError(116);
        }

        if (!double.TryParse(args[index], NumberStyles.Float, Inv, out var value))
        {
            throw new SimulatedError(118);
        }

        if (value < min - 1e-15 || value > max + 1e-15)
        {
            throw new SimulatedError(10);
        }

        return value;
    }

    private sealed class SimulatedError(int code) : Exception
    {
        public int Code { get; } = code;
    }

    /// <summary>Everything *SAV/*RCL store.</summary>
    private sealed class State
    {
        public int TriggerSource;
        public double TriggerRate = 10_000;
        public double TriggerLevel = 1.0;
        public double Holdoff;
        public int Inhibit;
        public int AdvancedTriggering;
        public long[] Prescale = [1, 1, 1, 1, 1];
        public long[] Phase = [0, 0, 0, 0, 0];
        public int BurstMode;
        public long BurstCount = 10;
        public double BurstDelay;
        public double BurstPeriod = 1e-6;
        public int BurstT0;
        public int[] Reference = new int[10];
        public double[] Delay = new double[10];
        public double[] DelayStep = Enumerable.Repeat(1e-9, 10).ToArray();
        public double[] Amplitude = [2.5, 2.5, 2.5, 2.5, 2.5];
        public double[] Offset = new double[5];
        public int[] Polarity = [1, 1, 1, 1, 1];
        public double[] AmplitudeStep = [0.1, 0.1, 0.1, 0.1, 0.1];
        public double[] OffsetStep = [0.1, 0.1, 0.1, 0.1, 0.1];

        public State Clone()
        {
            var copy = (State)MemberwiseClone();
            copy.Prescale = (long[])Prescale.Clone();
            copy.Phase = (long[])Phase.Clone();
            copy.Reference = (int[])Reference.Clone();
            copy.Delay = (double[])Delay.Clone();
            copy.DelayStep = (double[])DelayStep.Clone();
            copy.Amplitude = (double[])Amplitude.Clone();
            copy.Offset = (double[])Offset.Clone();
            copy.Polarity = (int[])Polarity.Clone();
            copy.AmplitudeStep = (double[])AmplitudeStep.Clone();
            copy.OffsetStep = (double[])OffsetStep.Clone();
            return copy;
        }
    }
}
