namespace StanfordController.Core.Protocol;

/// <summary>Delay channels, the <c>c</c>/<c>d</c> parameter of DLAY, LINK, SSDL and SPDL.</summary>
public enum Dg645Channel
{
    T0 = 0,
    T1 = 1,
    A = 2,
    B = 3,
    C = 4,
    D = 5,
    E = 6,
    F = 7,
    G = 8,
    H = 9,
}

/// <summary>Front-panel BNC outputs, the <c>b</c> parameter of LAMP, LOFF, LPOL, SSLA, SSLO, SPLA and SPLO.</summary>
public enum Dg645Output
{
    T0 = 0,
    AB = 1,
    CD = 2,
    EF = 3,
    GH = 4,
}

/// <summary>Prescaler index used by PRES (0-4) and PHAS (1-4).</summary>
public enum PrescaleInput
{
    Trigger = 0,
    AB = 1,
    CD = 2,
    EF = 3,
    GH = 4,
}

/// <summary>TSRC trigger source.</summary>
public enum TriggerSource
{
    Internal = 0,
    ExternalRising = 1,
    ExternalFalling = 2,
    SingleShotExternalRising = 3,
    SingleShotExternalFalling = 4,
    SingleShot = 5,
    Line = 6,
}

/// <summary>INHB inhibit mode.</summary>
public enum InhibitMode
{
    Off = 0,
    Triggers = 1,
    AB = 2,
    AB_CD = 3,
    AB_CD_EF = 4,
    AB_CD_EF_GH = 5,
}

/// <summary>LPOL output polarity.</summary>
public enum OutputPolarity
{
    Negative = 0,
    Positive = 1,
}

/// <summary>BURT burst T0 configuration.</summary>
public enum BurstT0Mode
{
    /// <summary>T0 fires on every delay cycle of the burst.</summary>
    EveryDelayCycle = 0,

    /// <summary>T0 fires only on the first delay cycle of the burst.</summary>
    FirstDelayCycleOnly = 1,
}

/// <summary>DISP front-panel display mode.</summary>
public enum DisplayMode
{
    TriggerRate = 0,
    TriggerThreshold = 1,
    TriggerSingleShot = 2,
    TriggerLine = 3,
    AdvancedTriggeringEnable = 4,
    TriggerHoldoff = 5,
    PrescaleConfiguration = 6,
    BurstMode = 7,
    BurstDelay = 8,
    BurstCount = 9,
    BurstPeriod = 10,
    ChannelDelay = 11,
    ChannelOutputLevels = 12,
    ChannelOutputPolarity = 13,
    BurstT0Configuration = 14,
}

/// <summary>IFCF interface configuration parameter index.</summary>
public enum InterfaceSetting
{
    Rs232Enable = 0,
    Rs232BaudRate = 1,
    GpibEnable = 2,
    GpibAddress = 3,
    LanEnable = 4,
    DhcpEnable = 5,
    AutoIpEnable = 6,
    StaticIpEnable = 7,
    BareSocketEnable = 8,
    TelnetEnable = 9,
    Vxi11Enable = 10,
    StaticIpAddress = 11,
    SubnetMask = 12,
    DefaultGateway = 13,
}

/// <summary>IFRS interface to reset.</summary>
public enum InterfaceKind
{
    Rs232 = 0,
    Gpib = 1,
    Lan = 2,
}

/// <summary>TIMB? timebase.</summary>
public enum Timebase
{
    Internal = 0,
    Ocxo = 1,
    Rubidium = 2,
    External = 3,
}

/// <summary>Instrument status register (INSR?/INSE) bits.</summary>
[Flags]
public enum InstrumentStatusFlags
{
    None = 0,
    Trigger = 1 << 0,
    Rate = 1 << 1,
    EndOfDelay = 1 << 2,
    EndOfBurst = 1 << 3,
    Inhibit = 1 << 4,
    AbortDelay = 1 << 5,
    PllUnlock = 1 << 6,
    RbUnlock = 1 << 7,
}

/// <summary>IEEE-488.2 standard event status register (*ESR?/*ESE) bits.</summary>
[Flags]
public enum StandardEventFlags
{
    None = 0,
    OperationComplete = 1 << 0,
    QueryError = 1 << 2,
    DeviceDependentError = 1 << 3,
    ExecutionError = 1 << 4,
    CommandError = 1 << 5,
    PowerOn = 1 << 7,
}

/// <summary>Serial poll status byte (*STB?/*SRE) bits.</summary>
[Flags]
public enum StatusByteFlags
{
    None = 0,
    InstrumentStatusSummary = 1 << 0,
    MessageAvailable = 1 << 4,
    EventStatusSummary = 1 << 5,
    MasterSummaryStatus = 1 << 6,
}
