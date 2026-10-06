namespace StanfordController.Core.Protocol;

/// <summary>Human-readable names for DG645 protocol values, shared by the UI, docs and logs.</summary>
public static class Dg645Names
{
    public static IReadOnlyList<Dg645Channel> DelayChannels { get; } =
    [
        Dg645Channel.A, Dg645Channel.B, Dg645Channel.C, Dg645Channel.D,
        Dg645Channel.E, Dg645Channel.F, Dg645Channel.G, Dg645Channel.H,
    ];

    public static IReadOnlyList<Dg645Output> Outputs { get; } =
    [
        Dg645Output.T0, Dg645Output.AB, Dg645Output.CD, Dg645Output.EF, Dg645Output.GH,
    ];

    public static IReadOnlyList<int> BaudRates { get; } = [4800, 9600, 19200, 38400, 57600, 115200];

    public static string Describe(TriggerSource source) => source switch
    {
        TriggerSource.Internal => "Internal",
        TriggerSource.ExternalRising => "External, rising edge",
        TriggerSource.ExternalFalling => "External, falling edge",
        TriggerSource.SingleShotExternalRising => "Single-shot external, rising edge",
        TriggerSource.SingleShotExternalFalling => "Single-shot external, falling edge",
        TriggerSource.SingleShot => "Single shot",
        TriggerSource.Line => "Line (AC mains)",
        _ => source.ToString(),
    };

    public static string Describe(InhibitMode mode) => mode switch
    {
        InhibitMode.Off => "Off",
        InhibitMode.Triggers => "Inhibit triggers",
        InhibitMode.AB => "Inhibit AB",
        InhibitMode.AB_CD => "Inhibit AB, CD",
        InhibitMode.AB_CD_EF => "Inhibit AB, CD, EF",
        InhibitMode.AB_CD_EF_GH => "Inhibit AB, CD, EF, GH",
        _ => mode.ToString(),
    };

    public static string Describe(OutputPolarity polarity) => polarity == OutputPolarity.Positive ? "Positive" : "Negative";

    public static string Describe(BurstT0Mode mode) => mode == BurstT0Mode.EveryDelayCycle
        ? "T0 on every delay cycle"
        : "T0 on first delay cycle only";

    public static string Describe(Timebase timebase) => timebase switch
    {
        Timebase.Internal => "Internal crystal",
        Timebase.Ocxo => "Optional OCXO",
        Timebase.Rubidium => "Optional rubidium",
        Timebase.External => "External reference",
        _ => timebase.ToString(),
    };

    public static string Describe(DisplayMode mode) => mode switch
    {
        DisplayMode.TriggerRate => "Trigger rate",
        DisplayMode.TriggerThreshold => "Trigger threshold",
        DisplayMode.TriggerSingleShot => "Trigger single shot",
        DisplayMode.TriggerLine => "Trigger line",
        DisplayMode.AdvancedTriggeringEnable => "Advanced triggering enable",
        DisplayMode.TriggerHoldoff => "Trigger holdoff",
        DisplayMode.PrescaleConfiguration => "Prescale configuration",
        DisplayMode.BurstMode => "Burst mode",
        DisplayMode.BurstDelay => "Burst delay",
        DisplayMode.BurstCount => "Burst count",
        DisplayMode.BurstPeriod => "Burst period",
        DisplayMode.ChannelDelay => "Channel delay",
        DisplayMode.ChannelOutputLevels => "Channel output levels",
        DisplayMode.ChannelOutputPolarity => "Channel output polarity",
        DisplayMode.BurstT0Configuration => "Burst T0 configuration",
        _ => mode.ToString(),
    };

    /// <summary>True when the DISP channel argument selects an output (T0, AB, ...) rather than a delay channel.</summary>
    public static bool DisplayModeUsesOutputs(DisplayMode mode) =>
        mode is DisplayMode.ChannelOutputLevels or DisplayMode.ChannelOutputPolarity;

    public static string Describe(InterfaceSetting setting) => setting switch
    {
        InterfaceSetting.Rs232Enable => "RS-232 enabled",
        InterfaceSetting.Rs232BaudRate => "RS-232 baud rate",
        InterfaceSetting.GpibEnable => "GPIB enabled",
        InterfaceSetting.GpibAddress => "GPIB address",
        InterfaceSetting.LanEnable => "LAN (TCP/IP) enabled",
        InterfaceSetting.DhcpEnable => "DHCP enabled",
        InterfaceSetting.AutoIpEnable => "Auto-IP enabled",
        InterfaceSetting.StaticIpEnable => "Static IP enabled",
        InterfaceSetting.BareSocketEnable => "Bare socket (port 5025) enabled",
        InterfaceSetting.TelnetEnable => "Telnet (port 5024) enabled",
        InterfaceSetting.Vxi11Enable => "VXI-11 net instrument enabled",
        InterfaceSetting.StaticIpAddress => "Static IP address",
        InterfaceSetting.SubnetMask => "Subnet mask",
        InterfaceSetting.DefaultGateway => "Default gateway",
        _ => setting.ToString(),
    };

    public static IReadOnlyList<StatusBitInfo> InstrumentStatusBits { get; } =
    [
        new(0, "TRIG", "Got a trigger."),
        new(1, "RATE", "Got a trigger while a delay or burst was in progress."),
        new(2, "END_OF_DELAY", "A delay cycle has completed."),
        new(3, "END_OF_BURST", "A burst cycle has completed."),
        new(4, "INHIBIT", "A trigger or output delay cycle was inhibited."),
        new(5, "ABORT_DELAY", "A delay cycle was aborted early."),
        new(6, "PLL_UNLOCK", "The 100 MHz PLL came unlocked."),
        new(7, "RB_UNLOCK", "The installed rubidium oscillator is unlocked."),
    ];

    public static IReadOnlyList<StatusBitInfo> StandardEventBits { get; } =
    [
        new(0, "OPC", "Operation complete. Set by *OPC when all prior commands have completed."),
        new(1, "—", "Unused."),
        new(2, "QYE", "Query error: output queue overflow or data lost."),
        new(3, "DDE", "Device dependent error."),
        new(4, "EXE", "Execution error: a command could not be executed (e.g. parameter out of range)."),
        new(5, "CME", "Command error: a command was not recognised or had a syntax error."),
        new(6, "—", "Unused."),
        new(7, "PON", "Power on: the instrument has been power cycled."),
    ];

    public static IReadOnlyList<StatusBitInfo> StatusByteBits { get; } =
    [
        new(0, "INSB", "Instrument status summary: an enabled bit in the instrument status register is set."),
        new(1, "—", "Unused."),
        new(2, "—", "Unused."),
        new(3, "—", "Unused."),
        new(4, "MAV", "Message available: the output queue is not empty."),
        new(5, "ESB", "Event status summary: an enabled bit in the standard event status register is set."),
        new(6, "MSS", "Master summary status: an enabled bit in the status byte is set."),
        new(7, "—", "Unused."),
    ];

    private static readonly Dictionary<int, string> ErrorCodes = new()
    {
        [0] = "No error",
        [10] = "Illegal value",
        [11] = "Illegal mode",
        [12] = "Illegal delay",
        [13] = "Illegal link",
        [14] = "Recall failed",
        [15] = "Not allowed",
        [16] = "Failed self test",
        [17] = "Failed auto calibration",
        [30] = "Lost data",
        [32] = "No listener",
        [40] = "Failed ROM check",
        [41] = "Failed offset T0 test",
        [42] = "Failed offset AB test",
        [43] = "Failed offset CD test",
        [44] = "Failed offset EF test",
        [45] = "Failed offset GH test",
        [46] = "Failed amplitude T0 test",
        [47] = "Failed amplitude AB test",
        [48] = "Failed amplitude CD test",
        [49] = "Failed amplitude EF test",
        [50] = "Failed amplitude GH test",
        [51] = "Failed FPGA communications test",
        [52] = "Failed GPIB communications test",
        [53] = "Failed DDS communications test",
        [54] = "Failed serial EEPROM communications test",
        [55] = "Failed temperature sensor communications test",
        [56] = "Failed PLL communications test",
        [57] = "Failed DAC 0 communications test",
        [58] = "Failed DAC 1 communications test",
        [59] = "Failed DAC 2 communications test",
        [60] = "Failed sample and hold operations test",
        [61] = "Failed Vjitter operations test",
        [62] = "Failed channel T0 analog delay test",
        [63] = "Failed channel T1 analog delay test",
        [64] = "Failed channel A analog delay test",
        [65] = "Failed channel B analog delay test",
        [66] = "Failed channel C analog delay test",
        [67] = "Failed channel D analog delay test",
        [68] = "Failed channel E analog delay test",
        [69] = "Failed channel F analog delay test",
        [70] = "Failed channel G analog delay test",
        [71] = "Failed channel H analog delay test",
        [80] = "Failed sample and hold calibration",
        [81] = "Failed T0 calibration",
        [82] = "Failed T1 calibration",
        [83] = "Failed A calibration",
        [84] = "Failed B calibration",
        [85] = "Failed C calibration",
        [86] = "Failed D calibration",
        [87] = "Failed E calibration",
        [88] = "Failed F calibration",
        [89] = "Failed G calibration",
        [90] = "Failed H calibration",
        [91] = "Failed Vjitter calibration",
        [110] = "Illegal command",
        [111] = "Undefined command",
        [112] = "Illegal query",
        [113] = "Illegal set",
        [114] = "Null parameter",
        [115] = "Extra parameters",
        [116] = "Missing parameters",
        [117] = "Parameter overflow",
        [118] = "Invalid floating point number",
        [120] = "Invalid integer",
        [121] = "Integer overflow",
        [122] = "Invalid hexadecimal",
        [126] = "Syntax error",
        [170] = "Communication error",
        [171] = "Over run",
        [254] = "Too many errors",
    };

    public static IReadOnlyDictionary<int, string> AllErrorCodes => ErrorCodes;

    public static string DescribeError(int code) =>
        ErrorCodes.TryGetValue(code, out var text) ? text : $"Unknown error {code}";
}

/// <summary>Description of one bit of a status register.</summary>
public sealed record StatusBitInfo(int Bit, string Name, string Description);
