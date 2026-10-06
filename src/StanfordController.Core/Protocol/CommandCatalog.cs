namespace StanfordController.Core.Protocol;

/// <summary>One DG645 remote command, as listed in the Remote Programming chapter of the manual.</summary>
/// <param name="Syntax">Manual notation: (?) means a query form exists, {braces} are optional parameters.</param>
/// <param name="Category">Command group, as in the manual's command index.</param>
/// <param name="Description">What the command does.</param>
/// <param name="Example">A ready-to-send example for the terminal.</param>
/// <param name="CrossChecked">
/// True when the command is also used by an independent, field-tested driver (EPICS delaygen, QCoDeS,
/// InstrumentKit or Labber). False for standard IEEE-488.2 / SRS commands not exercised by those drivers.
/// </param>
public sealed record CommandInfo(string Syntax, string Category, string Description, string Example, bool CrossChecked = true)
{
    public string Mnemonic => Syntax.Split(' ', '(', '?')[0];
}

/// <summary>The DG645 remote command set. Parameter letters: c = channel, b = output, i/j = integer, t = seconds, f = Hz, v = volts.</summary>
public static class CommandCatalog
{
    public const string Common = "IEEE-488.2 common";
    public const string Status = "Status & display";
    public const string Trigger = "Trigger";
    public const string Burst = "Burst";
    public const string Delay = "Delays & outputs";
    public const string Interface = "Interface";

    public static IReadOnlyList<CommandInfo> All { get; } =
    [
        new("*CAL?", Common, "Run auto calibration. Returns 0 on success or an error code.", "*CAL?"),
        new("*CLS", Common, "Clear all status registers.", "*CLS"),
        new("*ESE(?) {i}", Common, "Standard event status enable register (bit mask 0-255).", "*ESE?", CrossChecked: false),
        new("*ESR?", Common, "Standard event status register. Reading clears it.", "*ESR?", CrossChecked: false),
        new("*IDN?", Common, "Identification: manufacturer, model, serial number, firmware version.", "*IDN?"),
        new("*OPC(?)", Common, "Operation complete. The query returns 1 when all prior commands have finished.", "*OPC?", CrossChecked: false),
        new("*PSC(?) {i}", Common, "Power-on status clear: 1 clears the enable registers at power on.", "*PSC?", CrossChecked: false),
        new("*RCL i", Common, "Recall settings from location i (0 = factory defaults, 1-9 = saved settings).", "*RCL 1"),
        new("*RST", Common, "Reset the instrument to its default settings.", "*RST"),
        new("*SAV i", Common, "Save the current settings to location i (1-9).", "*SAV 1"),
        new("*SRE(?) {i}", Common, "Service request enable register (bit mask 0-255).", "*SRE?", CrossChecked: false),
        new("*STB?", Common, "Serial poll status byte.", "*STB?", CrossChecked: false),
        new("*TRG", Common, "Trigger a delay cycle when the trigger source is single shot.", "*TRG"),
        new("*TST?", Common, "Run the self test. Returns 0 on success or an error code.", "*TST?"),
        new("*WAI", Common, "Wait for all prior commands to finish before executing later ones.", "*WAI"),

        new("DISP(?) {i,j}", Status, "Front-panel display: i = display mode (0-14), j = channel or output shown.", "DISP?"),
        new("INSE(?) {i}", Status, "Instrument status enable register (bit mask 0-255).", "INSE?", CrossChecked: false),
        new("INSR?", Status, "Instrument status register: TRIG, RATE, END_OF_DELAY, END_OF_BURST, INHIBIT, ABORT_DELAY, PLL_UNLOCK, RB_UNLOCK. Reading clears it.", "INSR?"),
        new("LERR?", Status, "Last error from the error buffer (0 = no error). Reading removes it.", "LERR?"),
        new("TIMB?", Status, "Timebase in use: 0 internal, 1 OCXO, 2 rubidium, 3 external.", "TIMB?", CrossChecked: false),

        new("ADVT(?) {i}", Trigger, "Advanced triggering (prescalers, holdoff): 0 off, 1 on.", "ADVT?"),
        new("HOLD(?) {t}", Trigger, "Trigger holdoff in seconds.", "HOLD 1e-6"),
        new("INHB(?) {i}", Trigger, "Inhibit: 0 off, 1 triggers, 2 AB, 3 AB+CD, 4 AB+CD+EF, 5 AB+CD+EF+GH.", "INHB?"),
        new("PHAS(?) b{,i}", Trigger, "Prescale phase of output b (1 AB, 2 CD, 3 EF, 4 GH).", "PHAS?1"),
        new("PRES(?) b{,i}", Trigger, "Prescale factor: b = 0 trigger input, 1 AB, 2 CD, 3 EF, 4 GH.", "PRES?0"),
        new("TLVL(?) {v}", Trigger, "External trigger threshold in volts.", "TLVL 1.0"),
        new("TRAT(?) {f}", Trigger, "Internal trigger rate in Hz.", "TRAT 1000"),
        new("TSRC(?) {i}", Trigger, "Trigger source: 0 internal, 1 ext rising, 2 ext falling, 3 single-shot ext rising, 4 single-shot ext falling, 5 single shot, 6 line.", "TSRC?"),

        new("BURC(?) {i}", Burst, "Burst count (number of delay cycles per burst).", "BURC 10"),
        new("BURD(?) {t}", Burst, "Burst delay in seconds (from trigger to the first cycle).", "BURD 0"),
        new("BURM(?) {i}", Burst, "Burst mode: 0 off, 1 on.", "BURM?"),
        new("BURP(?) {t}", Burst, "Burst period in seconds (between cycles).", "BURP 1e-6"),
        new("BURT(?) {i}", Burst, "Burst T0 configuration: 0 T0 on every cycle, 1 first cycle only.", "BURT?"),

        new("DLAY(?) c{,d,t}", Delay, "Delay of channel c: c = d + t. Channels: 0 T0, 1 T1, 2 A, 3 B, 4 C, 5 D, 6 E, 7 F, 8 G, 9 H.", "DLAY 2,0,1e-6"),
        new("LAMP(?) b{,v}", Delay, "Output amplitude in volts. Outputs: 0 T0, 1 AB, 2 CD, 3 EF, 4 GH.", "LAMP?1"),
        new("LINK(?) c{,d}", Delay, "Link channel c to reference channel d.", "LINK?3"),
        new("LOFF(?) b{,v}", Delay, "Output offset in volts.", "LOFF?1"),
        new("LPOL(?) b{,i}", Delay, "Output polarity: 0 negative, 1 positive.", "LPOL?1"),
        new("SPDL c,i", Delay, "Step channel c's delay down (i = 0) or up (i = 1) by its step size.", "SPDL 2,1"),
        new("SPLA b,i", Delay, "Step output b's amplitude down (0) or up (1).", "SPLA 1,1"),
        new("SPLO b,i", Delay, "Step output b's offset down (0) or up (1).", "SPLO 1,1"),
        new("SSDL(?) c{,t}", Delay, "Step size of channel c's delay in seconds.", "SSDL?2"),
        new("SSLA(?) b{,v}", Delay, "Step size of output b's amplitude in volts.", "SSLA?1"),
        new("SSLO(?) b{,v}", Delay, "Step size of output b's offset in volts.", "SSLO?1"),

        new("EMAC?", Interface, "Ethernet MAC address.", "EMAC?"),
        new("IFCF(?) i{,j}", Interface, "Interface configuration: 0 RS-232 on, 1 baud, 2 GPIB on, 3 GPIB address, 4 LAN on, 5 DHCP, 6 Auto-IP, 7 static IP, 8 bare socket, 9 telnet, 10 VXI-11, 11 IP address, 12 subnet mask, 13 gateway.", "IFCF?11"),
        new("IFRS i", Interface, "Reset interface i (0 RS-232, 1 GPIB, 2 LAN) so new settings take effect.", "IFRS 2"),
        new("LCAL", Interface, "Return the front panel to local control.", "LCAL"),
        new("LOCK?", Interface, "Request exclusive control lock. Returns 1 if granted.", "LOCK?", CrossChecked: false),
        new("REMT", Interface, "Put the instrument in remote mode (front panel locked).", "REMT"),
        new("UNLK?", Interface, "Release the control lock. Returns 1 if it was held.", "UNLK?", CrossChecked: false),
    ];
}
