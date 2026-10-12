using StanfordController.Core.Protocol;
using StanfordController.Core.Transport;
using F = StanfordController.Core.Protocol.Dg645Format;

namespace StanfordController.Core;

public enum CommunicationDirection
{
    Sent,
    Received,
    Info,
    Error,
}

public sealed class CommunicationEventArgs(CommunicationDirection direction, string text) : EventArgs
{
    public CommunicationDirection Direction { get; } = direction;

    public string Text { get; } = text;

    public DateTime Timestamp { get; } = DateTime.Now;
}

/// <summary>The instrument reported an error (via LERR?) after a command.</summary>
public sealed class Dg645Exception(string command, int errorCode)
    : Exception($"The instrument rejected '{command}': {Dg645Names.DescribeError(errorCode)} (error {errorCode}).")
{
    public string Command { get; } = command;

    public int ErrorCode { get; } = errorCode;

    public string ErrorDescription => Dg645Names.DescribeError(ErrorCode);
}

/// <summary>
/// The instrument does not know a command (error 111, undefined command): its firmware is older than the command.
/// Once this has happened, the client stops sending that command for the rest of the connection.
/// </summary>
public sealed class Dg645UnsupportedCommandException(string command)
    : Exception($"This instrument's firmware does not have the {Dg645Client.Mnemonic(command)} command.")
{
    public string Command { get; } = command;

    /// <summary>The command's name, e.g. PRES for <c>PRES?1</c>.</summary>
    public string Mnemonic { get; } = Dg645Client.Mnemonic(command);
}

/// <summary>Responses and errors from a raw terminal command.</summary>
public sealed record RawCommandResult(IReadOnlyList<string> Responses, IReadOnlyList<int> Errors, bool TimedOut);

/// <summary>
/// Typed access to every DG645 remote command. All calls are serialised, so the client can be shared
/// by the UI and a background status poller.
/// </summary>
public sealed class Dg645Client(IDg645Transport transport) : IAsyncDisposable
{
    /// <summary>The DG645's error for a command it does not know.</summary>
    public const int UndefinedCommandError = 111;

    private static readonly AsyncLocal<bool> QuietTraffic = new();
    private static readonly AsyncLocal<bool> AuditedChange = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _unsupported = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Timeout for self test and auto calibration, which take several seconds.</summary>
    public TimeSpan LongOperationTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public string Description => transport.Description;

    /// <summary>The connection in use; for the simulator, its <see cref="SimulatorTransport.Simulator"/>.</summary>
    public IDg645Transport Transport => transport;

    public bool IsConnected => transport.IsConnected;

    public string? Identity { get; private set; }

    /// <summary>Commands this instrument's firmware does not have: found while connected, or given to <see cref="AssumeUnsupported"/>.</summary>
    public IReadOnlyList<string> UnsupportedCommands
    {
        get
        {
            lock (_unsupported)
            {
                return _unsupported.Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    public event EventHandler<CommunicationEventArgs>? Communication;

    /// <summary>
    /// Raised (on a background thread) for every line sent that can change the instrument, meaning any part of it
    /// is not a query, unless it was sent inside <see cref="BeginAuditedChange"/>. The audit trail records these, so
    /// a change is never missed even if it was made by a path that does not record its own.
    /// </summary>
    public event EventHandler<string>? UnrecordedCommandSent;

    // ------------------------------------------------------------------------------------------
    // Connection
    // ------------------------------------------------------------------------------------------

    public async Task<string> ConnectAsync(CancellationToken ct = default)
    {
        Log(CommunicationDirection.Info, $"Connecting to {Description}…");
        await transport.ConnectAsync(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await transport.DrainAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            Identity = await QueryCoreAsync("*IDN?", QueryTimeout, ct, diagnose: false).ConfigureAwait(false);

            // Start with an empty error queue so later errors belong to our own commands.
            var stale = await ReadErrorQueueCoreAsync(ct).ConfigureAwait(false);
            if (stale.Count > 0)
            {
                Log(CommunicationDirection.Info, $"Cleared {stale.Count} earlier error(s) from the instrument's error queue.");
            }
        }
        finally
        {
            _gate.Release();
        }

        Log(CommunicationDirection.Info, $"Connected: {Identity}");
        return Identity;
    }

    /// <summary>Closes the link, optionally returning the front panel to local control first.</summary>
    public async Task DisconnectAsync(bool returnToLocal = true)
    {
        if (returnToLocal && transport.IsConnected)
        {
            try
            {
                await SendAsync("LCAL").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log(CommunicationDirection.Error, $"Could not return the instrument to local control: {ex.Message}");
            }
        }

        await transport.DisposeAsync().ConfigureAwait(false);
        Log(CommunicationDirection.Info, "Disconnected.");
    }

    public ValueTask DisposeAsync() => transport.DisposeAsync();

    // ------------------------------------------------------------------------------------------
    // Firmware
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Commands already known to be missing from this instrument's firmware, so they are not sent (and waited for)
    /// again. Typed commands in the terminal are always sent.
    /// </summary>
    public void AssumeUnsupported(IEnumerable<string> commands)
    {
        lock (_unsupported)
        {
            foreach (var command in commands)
            {
                _unsupported.Add(Mnemonic(command));
            }
        }
    }

    /// <summary>False once the firmware has turned out not to have <paramref name="command"/>.</summary>
    public bool Supports(string command)
    {
        lock (_unsupported)
        {
            return !_unsupported.Contains(Mnemonic(command));
        }
    }

    /// <summary>A command's name: <c>PRES?1</c> gives PRES, <c>*IDN?</c> gives *IDN.</summary>
    public static string Mnemonic(string command)
    {
        var text = command.TrimStart();
        var end = text.StartsWith('*') ? 1 : 0;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }

        return text[..end].ToUpperInvariant();
    }

    private void ThrowIfUnsupported(string command)
    {
        if (!Supports(command))
        {
            throw new Dg645UnsupportedCommandException(command);
        }
    }

    private void MarkUnsupported(string command)
    {
        var mnemonic = Mnemonic(command);
        bool added;
        lock (_unsupported)
        {
            added = _unsupported.Add(mnemonic);
        }

        if (added)
        {
            Log(CommunicationDirection.Info,
                $"This instrument's firmware does not have the {mnemonic} command (error {UndefinedCommandError}); it will not be sent again.");
        }
    }

    // ------------------------------------------------------------------------------------------
    // Raw access
    // ------------------------------------------------------------------------------------------

    /// <summary>Sends a command without checking the error queue.</summary>
    public Task SendAsync(string command, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            ThrowIfUnsupported(command);
            return WriteCoreAsync(command, ct);
        }, ct);

    /// <summary>
    /// Sends a command, then reads LERR? and throws <see cref="Dg645Exception"/> if it was rejected, or
    /// <see cref="Dg645UnsupportedCommandException"/> if the firmware does not have the command.
    /// </summary>
    public Task ExecuteAsync(string command, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            ThrowIfUnsupported(command);
            await WriteCoreAsync(command, ct).ConfigureAwait(false);
            var errors = await ReadErrorQueueCoreAsync(ct).ConfigureAwait(false);
            if (errors is [UndefinedCommandError] && !command.Contains(';'))
            {
                MarkUnsupported(command);
                throw new Dg645UnsupportedCommandException(command);
            }

            if (errors.Count > 0)
            {
                throw new Dg645Exception(command, errors[0]);
            }
        }, ct);

    /// <summary>
    /// Sends a query and returns the reply. Throws <see cref="Dg645UnsupportedCommandException"/> if the firmware does
    /// not have the command, and <see cref="TimeoutException"/> if there is no reply for another reason.
    /// </summary>
    public Task<string> QueryAsync(string query, TimeSpan? timeout = null, CancellationToken ct = default) =>
        RunAsync(() =>
        {
            ThrowIfUnsupported(query);
            return QueryCoreAsync(query, timeout ?? QueryTimeout, ct);
        }, ct);

    /// <summary>
    /// Sends a line typed by the user. Collects every response line (a line may hold several
    /// ';'-separated queries) and, optionally, the error queue.
    /// </summary>
    public Task<RawCommandResult> ExecuteRawAsync(string line, bool checkErrors, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            await transport.DrainAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
            await WriteCoreAsync(line, ct).ConfigureAwait(false);

            var responses = new List<string>();
            var timedOut = false;
            if (line.Contains('?'))
            {
                try
                {
                    responses.Add(await ReadCoreAsync(QueryTimeout, ct).ConfigureAwait(false));
                    while (true)
                    {
                        responses.Add(await ReadCoreAsync(TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(false));
                    }
                }
                catch (TimeoutException)
                {
                    timedOut = responses.Count == 0;
                }
            }

            var errors = checkErrors || timedOut
                ? await ReadErrorQueueCoreAsync(ct).ConfigureAwait(false)
                : (IReadOnlyList<int>)[];
            return new RawCommandResult(responses, errors, timedOut);
        }, ct);

    // ------------------------------------------------------------------------------------------
    // IEEE-488.2 common commands
    // ------------------------------------------------------------------------------------------

    public Task<string> GetIdentityAsync(CancellationToken ct = default) => QueryAsync("*IDN?", ct: ct);

    public Task ResetAsync(CancellationToken ct = default) => ExecuteAsync("*RST", ct);

    public Task ClearStatusAsync(CancellationToken ct = default) => SendAsync("*CLS", ct);

    public Task TriggerAsync(CancellationToken ct = default) => ExecuteAsync("*TRG", ct);

    public Task WaitAsync(CancellationToken ct = default) => SendAsync("*WAI", ct);

    /// <summary>*SAV: stores the current settings in non-volatile location 1-9.</summary>
    public Task SaveSettingsAsync(int location, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(location, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(location, 9);
        return ExecuteAsync($"*SAV {location}", ct);
    }

    /// <summary>*RCL: recalls location 1-9, or 0 for the factory default settings.</summary>
    public Task RecallSettingsAsync(int location, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(location);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(location, 9);
        return ExecuteAsync($"*RCL {location}", ct);
    }

    /// <summary>*TST?: runs the self test. Returns 0 on success, otherwise an error code.</summary>
    public async Task<int> SelfTestAsync(CancellationToken ct = default) =>
        F.ParseInt(await QueryAsync("*TST?", LongOperationTimeout, ct).ConfigureAwait(false));

    /// <summary>*CAL?: runs auto calibration. Returns 0 on success, otherwise an error code.</summary>
    public async Task<int> AutoCalibrateAsync(CancellationToken ct = default) =>
        F.ParseInt(await QueryAsync("*CAL?", LongOperationTimeout, ct).ConfigureAwait(false));

    /// <summary>*OPC?: returns once all pending operations have completed.</summary>
    public async Task<bool> OperationCompleteAsync(CancellationToken ct = default) =>
        F.ParseBool(await QueryAsync("*OPC?", LongOperationTimeout, ct).ConfigureAwait(false));

    public async Task<StandardEventFlags> GetStandardEventStatusAsync(CancellationToken ct = default) =>
        (StandardEventFlags)await QueryIntAsync("*ESR?", ct).ConfigureAwait(false);

    public async Task<StandardEventFlags> GetStandardEventEnableAsync(CancellationToken ct = default) =>
        (StandardEventFlags)await QueryIntAsync("*ESE?", ct).ConfigureAwait(false);

    public Task SetStandardEventEnableAsync(StandardEventFlags mask, CancellationToken ct = default) =>
        ExecuteAsync($"*ESE {(int)mask}", ct);

    public async Task<StatusByteFlags> GetStatusByteAsync(CancellationToken ct = default) =>
        (StatusByteFlags)await QueryIntAsync("*STB?", ct).ConfigureAwait(false);

    public async Task<StatusByteFlags> GetServiceRequestEnableAsync(CancellationToken ct = default) =>
        (StatusByteFlags)await QueryIntAsync("*SRE?", ct).ConfigureAwait(false);

    public Task SetServiceRequestEnableAsync(StatusByteFlags mask, CancellationToken ct = default) =>
        ExecuteAsync($"*SRE {(int)mask}", ct);

    public async Task<bool> GetPowerOnStatusClearAsync(CancellationToken ct = default) =>
        await QueryIntAsync("*PSC?", ct).ConfigureAwait(false) != 0;

    public Task SetPowerOnStatusClearAsync(bool clear, CancellationToken ct = default) =>
        ExecuteAsync($"*PSC {(clear ? 1 : 0)}", ct);

    // ------------------------------------------------------------------------------------------
    // Status and display
    // ------------------------------------------------------------------------------------------

    /// <summary>INSR?: reads (and clears) the instrument status register.</summary>
    public async Task<InstrumentStatusFlags> GetInstrumentStatusAsync(CancellationToken ct = default) =>
        (InstrumentStatusFlags)await QueryIntAsync("INSR?", ct).ConfigureAwait(false);

    public async Task<InstrumentStatusFlags> GetInstrumentStatusEnableAsync(CancellationToken ct = default) =>
        (InstrumentStatusFlags)await QueryIntAsync("INSE?", ct).ConfigureAwait(false);

    public Task SetInstrumentStatusEnableAsync(InstrumentStatusFlags mask, CancellationToken ct = default) =>
        ExecuteAsync($"INSE {(int)mask}", ct);

    /// <summary>LERR?: pops the last error from the error buffer (0 = no error).</summary>
    public Task<int> GetLastErrorAsync(CancellationToken ct = default) => QueryIntAsync("LERR?", ct);

    /// <summary>Reads LERR? until the queue is empty.</summary>
    public Task<IReadOnlyList<int>> ReadErrorQueueAsync(CancellationToken ct = default) =>
        RunAsync(() => ReadErrorQueueCoreAsync(ct), ct);

    public async Task<Timebase> GetTimebaseAsync(CancellationToken ct = default) =>
        (Timebase)await QueryIntAsync("TIMB?", ct).ConfigureAwait(false);

    public async Task<(DisplayMode Mode, int Channel)> GetDisplayAsync(CancellationToken ct = default)
    {
        var parts = await QueryPartsAsync("DISP?", 2, ct).ConfigureAwait(false);
        return ((DisplayMode)F.ParseInt(parts[0]), F.ParseInt(parts[1]));
    }

    /// <summary>DISP: selects what the front panel shows. <paramref name="channel"/> is a delay channel or output index.</summary>
    public Task SetDisplayAsync(DisplayMode mode, int channel, CancellationToken ct = default) =>
        ExecuteAsync($"DISP {(int)mode},{channel}", ct);

    // ------------------------------------------------------------------------------------------
    // Trigger
    // ------------------------------------------------------------------------------------------

    public async Task<TriggerSource> GetTriggerSourceAsync(CancellationToken ct = default) =>
        (TriggerSource)await QueryIntAsync("TSRC?", ct).ConfigureAwait(false);

    public Task SetTriggerSourceAsync(TriggerSource source, CancellationToken ct = default) =>
        ExecuteAsync($"TSRC {(int)source}", ct);

    public Task<double> GetTriggerRateAsync(CancellationToken ct = default) => QueryDoubleAsync("TRAT?", ct);

    public Task SetTriggerRateAsync(double hertz, CancellationToken ct = default) =>
        ExecuteAsync($"TRAT {F.Frequency(hertz)}", ct);

    public Task<double> GetTriggerLevelAsync(CancellationToken ct = default) => QueryDoubleAsync("TLVL?", ct);

    public Task SetTriggerLevelAsync(double volts, CancellationToken ct = default) =>
        ExecuteAsync($"TLVL {F.Volts(volts)}", ct);

    public Task<double> GetHoldoffAsync(CancellationToken ct = default) => QueryDoubleAsync("HOLD?", ct);

    public Task SetHoldoffAsync(double seconds, CancellationToken ct = default) =>
        ExecuteAsync($"HOLD {F.Time(seconds)}", ct);

    public async Task<InhibitMode> GetInhibitAsync(CancellationToken ct = default) =>
        (InhibitMode)await QueryIntAsync("INHB?", ct).ConfigureAwait(false);

    public Task SetInhibitAsync(InhibitMode mode, CancellationToken ct = default) =>
        ExecuteAsync($"INHB {(int)mode}", ct);

    public async Task<bool> GetAdvancedTriggeringAsync(CancellationToken ct = default) =>
        await QueryIntAsync("ADVT?", ct).ConfigureAwait(false) != 0;

    public Task SetAdvancedTriggeringAsync(bool enabled, CancellationToken ct = default) =>
        ExecuteAsync($"ADVT {(enabled ? 1 : 0)}", ct);

    public Task<long> GetPrescaleFactorAsync(PrescaleInput input, CancellationToken ct = default) =>
        QueryLongAsync($"PRES?{(int)input}", ct);

    public Task SetPrescaleFactorAsync(PrescaleInput input, long factor, CancellationToken ct = default) =>
        ExecuteAsync($"PRES {(int)input},{factor}", ct);

    public Task<long> GetPrescalePhaseAsync(PrescaleInput output, CancellationToken ct = default)
    {
        EnsureOutputPrescaler(output);
        return QueryLongAsync($"PHAS?{(int)output}", ct);
    }

    public Task SetPrescalePhaseAsync(PrescaleInput output, long phase, CancellationToken ct = default)
    {
        EnsureOutputPrescaler(output);
        return ExecuteAsync($"PHAS {(int)output},{phase}", ct);
    }

    // ------------------------------------------------------------------------------------------
    // Burst
    // ------------------------------------------------------------------------------------------

    public async Task<bool> GetBurstModeAsync(CancellationToken ct = default) =>
        await QueryIntAsync("BURM?", ct).ConfigureAwait(false) != 0;

    public Task SetBurstModeAsync(bool enabled, CancellationToken ct = default) =>
        ExecuteAsync($"BURM {(enabled ? 1 : 0)}", ct);

    public Task<long> GetBurstCountAsync(CancellationToken ct = default) => QueryLongAsync("BURC?", ct);

    public Task SetBurstCountAsync(long count, CancellationToken ct = default) =>
        ExecuteAsync($"BURC {count}", ct);

    public Task<double> GetBurstDelayAsync(CancellationToken ct = default) => QueryDoubleAsync("BURD?", ct);

    public Task SetBurstDelayAsync(double seconds, CancellationToken ct = default) =>
        ExecuteAsync($"BURD {F.Time(seconds)}", ct);

    public Task<double> GetBurstPeriodAsync(CancellationToken ct = default) => QueryDoubleAsync("BURP?", ct);

    public Task SetBurstPeriodAsync(double seconds, CancellationToken ct = default) =>
        ExecuteAsync($"BURP {F.Time(seconds)}", ct);

    public async Task<BurstT0Mode> GetBurstT0ModeAsync(CancellationToken ct = default) =>
        (BurstT0Mode)await QueryIntAsync("BURT?", ct).ConfigureAwait(false);

    public Task SetBurstT0ModeAsync(BurstT0Mode mode, CancellationToken ct = default) =>
        ExecuteAsync($"BURT {(int)mode}", ct);

    // ------------------------------------------------------------------------------------------
    // Delays
    // ------------------------------------------------------------------------------------------

    /// <summary>DLAY?: the channel's reference channel and its delay from that reference.</summary>
    public async Task<(Dg645Channel Reference, double Seconds)> GetDelayAsync(Dg645Channel channel, CancellationToken ct = default)
    {
        var parts = await QueryPartsAsync($"DLAY?{(int)channel}", 2, ct).ConfigureAwait(false);
        return ((Dg645Channel)F.ParseInt(parts[0]), F.ParseDouble(parts[1]));
    }

    /// <summary>DLAY: sets channel = reference + seconds.</summary>
    public Task SetDelayAsync(Dg645Channel channel, Dg645Channel reference, double seconds, CancellationToken ct = default) =>
        ExecuteAsync($"DLAY {(int)channel},{(int)reference},{F.Time(seconds)}", ct);

    public async Task<Dg645Channel> GetLinkAsync(Dg645Channel channel, CancellationToken ct = default) =>
        (Dg645Channel)await QueryIntAsync($"LINK?{(int)channel}", ct).ConfigureAwait(false);

    /// <summary>LINK: makes <paramref name="channel"/> relative to <paramref name="reference"/>.</summary>
    public Task SetLinkAsync(Dg645Channel channel, Dg645Channel reference, CancellationToken ct = default) =>
        ExecuteAsync($"LINK {(int)channel},{(int)reference}", ct);

    public Task<double> GetDelayStepAsync(Dg645Channel channel, CancellationToken ct = default) =>
        QueryDoubleAsync($"SSDL?{(int)channel}", ct);

    public Task SetDelayStepAsync(Dg645Channel channel, double seconds, CancellationToken ct = default) =>
        ExecuteAsync($"SSDL {(int)channel},{F.Time(seconds)}", ct);

    /// <summary>SPDL: steps the channel delay up or down by its step size.</summary>
    public Task StepDelayAsync(Dg645Channel channel, bool up, CancellationToken ct = default) =>
        ExecuteAsync($"SPDL {(int)channel},{(up ? 1 : 0)}", ct);

    // ------------------------------------------------------------------------------------------
    // Output levels
    // ------------------------------------------------------------------------------------------

    public Task<double> GetAmplitudeAsync(Dg645Output output, CancellationToken ct = default) =>
        QueryDoubleAsync($"LAMP?{(int)output}", ct);

    public Task SetAmplitudeAsync(Dg645Output output, double volts, CancellationToken ct = default) =>
        ExecuteAsync($"LAMP {(int)output},{F.Volts(volts)}", ct);

    public Task<double> GetOffsetAsync(Dg645Output output, CancellationToken ct = default) =>
        QueryDoubleAsync($"LOFF?{(int)output}", ct);

    public Task SetOffsetAsync(Dg645Output output, double volts, CancellationToken ct = default) =>
        ExecuteAsync($"LOFF {(int)output},{F.Volts(volts)}", ct);

    public async Task<OutputPolarity> GetPolarityAsync(Dg645Output output, CancellationToken ct = default) =>
        (OutputPolarity)await QueryIntAsync($"LPOL?{(int)output}", ct).ConfigureAwait(false);

    public Task SetPolarityAsync(Dg645Output output, OutputPolarity polarity, CancellationToken ct = default) =>
        ExecuteAsync($"LPOL {(int)output},{(int)polarity}", ct);

    public Task<double> GetAmplitudeStepAsync(Dg645Output output, CancellationToken ct = default) =>
        QueryDoubleAsync($"SSLA?{(int)output}", ct);

    public Task SetAmplitudeStepAsync(Dg645Output output, double volts, CancellationToken ct = default) =>
        ExecuteAsync($"SSLA {(int)output},{F.Volts(volts)}", ct);

    public Task<double> GetOffsetStepAsync(Dg645Output output, CancellationToken ct = default) =>
        QueryDoubleAsync($"SSLO?{(int)output}", ct);

    public Task SetOffsetStepAsync(Dg645Output output, double volts, CancellationToken ct = default) =>
        ExecuteAsync($"SSLO {(int)output},{F.Volts(volts)}", ct);

    public Task StepAmplitudeAsync(Dg645Output output, bool up, CancellationToken ct = default) =>
        ExecuteAsync($"SPLA {(int)output},{(up ? 1 : 0)}", ct);

    public Task StepOffsetAsync(Dg645Output output, bool up, CancellationToken ct = default) =>
        ExecuteAsync($"SPLO {(int)output},{(up ? 1 : 0)}", ct);

    // ------------------------------------------------------------------------------------------
    // Interfaces
    // ------------------------------------------------------------------------------------------

    public Task<string> GetInterfaceSettingAsync(InterfaceSetting setting, CancellationToken ct = default) =>
        QueryAsync($"IFCF?{(int)setting}", ct: ct);

    /// <summary>IFCF: most changes take effect after <see cref="ResetInterfaceAsync"/> or a power cycle.</summary>
    public Task SetInterfaceSettingAsync(InterfaceSetting setting, string value, CancellationToken ct = default) =>
        ExecuteAsync($"IFCF {(int)setting},{value.Trim()}", ct);

    /// <summary>IFRS: resets an interface so pending IFCF changes take effect. Resetting LAN drops this connection.</summary>
    public Task ResetInterfaceAsync(InterfaceKind kind, CancellationToken ct = default) =>
        SendAsync($"IFRS {(int)kind}", ct);

    public Task<string> GetMacAddressAsync(CancellationToken ct = default) => QueryAsync("EMAC?", ct: ct);

    public Task GoToLocalAsync(CancellationToken ct = default) => SendAsync("LCAL", ct);

    public Task GoToRemoteAsync(CancellationToken ct = default) => SendAsync("REMT", ct);

    /// <summary>LOCK?: requests exclusive control. True if the lock was granted.</summary>
    public async Task<bool> RequestLockAsync(CancellationToken ct = default) =>
        await QueryIntAsync("LOCK?", ct).ConfigureAwait(false) != 0;

    /// <summary>UNLK?: releases the lock. True if this interface held it.</summary>
    public async Task<bool> ReleaseLockAsync(CancellationToken ct = default) =>
        await QueryIntAsync("UNLK?", ct).ConfigureAwait(false) != 0;

    // ------------------------------------------------------------------------------------------
    // Whole-instrument snapshots
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the instrument's whole setup. A setting its firmware does not have keeps its default value (see
    /// <see cref="UnsupportedCommands"/>), so older firmware still gives a complete snapshot.
    /// </summary>
    public async Task<Dg645Settings> ReadSettingsAsync(CancellationToken ct = default)
    {
        var settings = new Dg645Settings { SourceIdentity = Identity, SavedUtc = DateTime.UtcNow };

        var trigger = settings.Trigger;
        trigger.Source = await OrDefaultAsync(GetTriggerSourceAsync(ct), trigger.Source).ConfigureAwait(false);
        trigger.RateHz = await OrDefaultAsync(GetTriggerRateAsync(ct), trigger.RateHz).ConfigureAwait(false);
        trigger.LevelVolts = await OrDefaultAsync(GetTriggerLevelAsync(ct), trigger.LevelVolts).ConfigureAwait(false);
        trigger.HoldoffSeconds = await OrDefaultAsync(GetHoldoffAsync(ct), trigger.HoldoffSeconds).ConfigureAwait(false);
        trigger.Inhibit = await OrDefaultAsync(GetInhibitAsync(ct), trigger.Inhibit).ConfigureAwait(false);
        trigger.AdvancedTriggering = await OrDefaultAsync(GetAdvancedTriggeringAsync(ct), trigger.AdvancedTriggering).ConfigureAwait(false);
        trigger.PrescaleFactors.Clear();
        trigger.PrescalePhases.Clear();
        foreach (var input in Enum.GetValues<PrescaleInput>())
        {
            trigger.PrescaleFactors.Add(await OrDefaultAsync(GetPrescaleFactorAsync(input, ct), 1L).ConfigureAwait(false));
            if (input != PrescaleInput.Trigger)
            {
                trigger.PrescalePhases.Add(await OrDefaultAsync(GetPrescalePhaseAsync(input, ct), 0L).ConfigureAwait(false));
            }
        }

        var burst = settings.Burst;
        burst.Enabled = await OrDefaultAsync(GetBurstModeAsync(ct), burst.Enabled).ConfigureAwait(false);
        burst.Count = await OrDefaultAsync(GetBurstCountAsync(ct), burst.Count).ConfigureAwait(false);
        burst.DelaySeconds = await OrDefaultAsync(GetBurstDelayAsync(ct), burst.DelaySeconds).ConfigureAwait(false);
        burst.PeriodSeconds = await OrDefaultAsync(GetBurstPeriodAsync(ct), burst.PeriodSeconds).ConfigureAwait(false);
        burst.T0Mode = await OrDefaultAsync(GetBurstT0ModeAsync(ct), burst.T0Mode).ConfigureAwait(false);

        foreach (var channel in Dg645Names.DelayChannels)
        {
            var delay = new ChannelDelaySettings { Channel = channel };
            (delay.Reference, delay.DelaySeconds) =
                await OrDefaultAsync(GetDelayAsync(channel, ct), (delay.Reference, delay.DelaySeconds)).ConfigureAwait(false);
            settings.Delays.Add(delay);
        }

        foreach (var output in Dg645Names.Outputs)
        {
            var levels = new OutputLevelSettings { Output = output };
            levels.AmplitudeVolts = await OrDefaultAsync(GetAmplitudeAsync(output, ct), levels.AmplitudeVolts).ConfigureAwait(false);
            levels.OffsetVolts = await OrDefaultAsync(GetOffsetAsync(output, ct), levels.OffsetVolts).ConfigureAwait(false);
            levels.Polarity = await OrDefaultAsync(GetPolarityAsync(output, ct), levels.Polarity).ConfigureAwait(false);
            settings.Outputs.Add(levels);
        }

        return settings;
    }

    /// <summary>
    /// Writes a snapshot to the instrument. Stops at the first rejected command; a setting the firmware does not have
    /// is left out.
    /// </summary>
    public async Task ApplySettingsAsync(Dg645Settings settings, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var trigger = settings.Trigger;
        progress?.Report("Trigger");
        await SkipIfUnsupportedAsync(SetAdvancedTriggeringAsync(trigger.AdvancedTriggering, ct)).ConfigureAwait(false);
        for (var i = 0; i < trigger.PrescaleFactors.Count && i <= (int)PrescaleInput.GH; i++)
        {
            await SkipIfUnsupportedAsync(SetPrescaleFactorAsync((PrescaleInput)i, trigger.PrescaleFactors[i], ct)).ConfigureAwait(false);
        }

        for (var i = 0; i < trigger.PrescalePhases.Count && i < 4; i++)
        {
            await SkipIfUnsupportedAsync(SetPrescalePhaseAsync((PrescaleInput)(i + 1), trigger.PrescalePhases[i], ct)).ConfigureAwait(false);
        }

        await SkipIfUnsupportedAsync(SetTriggerSourceAsync(trigger.Source, ct)).ConfigureAwait(false);
        await SkipIfUnsupportedAsync(SetTriggerRateAsync(trigger.RateHz, ct)).ConfigureAwait(false);
        await SkipIfUnsupportedAsync(SetTriggerLevelAsync(trigger.LevelVolts, ct)).ConfigureAwait(false);
        await SkipIfUnsupportedAsync(SetHoldoffAsync(trigger.HoldoffSeconds, ct)).ConfigureAwait(false);
        await SkipIfUnsupportedAsync(SetInhibitAsync(trigger.Inhibit, ct)).ConfigureAwait(false);

        progress?.Report("Burst");
        var burst = settings.Burst;
        await SkipIfUnsupportedAsync(SetBurstCountAsync(burst.Count, ct)).ConfigureAwait(false);
        await SkipIfUnsupportedAsync(SetBurstPeriodAsync(burst.PeriodSeconds, ct)).ConfigureAwait(false);
        await SkipIfUnsupportedAsync(SetBurstDelayAsync(burst.DelaySeconds, ct)).ConfigureAwait(false);
        await SkipIfUnsupportedAsync(SetBurstT0ModeAsync(burst.T0Mode, ct)).ConfigureAwait(false);
        await SkipIfUnsupportedAsync(SetBurstModeAsync(burst.Enabled, ct)).ConfigureAwait(false);

        progress?.Report("Outputs");
        foreach (var output in settings.Outputs)
        {
            await SkipIfUnsupportedAsync(SetAmplitudeAsync(output.Output, output.AmplitudeVolts, ct)).ConfigureAwait(false);
            await SkipIfUnsupportedAsync(SetOffsetAsync(output.Output, output.OffsetVolts, ct)).ConfigureAwait(false);
            await SkipIfUnsupportedAsync(SetPolarityAsync(output.Output, output.Polarity, ct)).ConfigureAwait(false);
        }

        // Re-reference every channel to T0 first so the new links can never form a temporary cycle
        // with the instrument's current ones (which it would reject as an illegal link).
        progress?.Report("Delays");
        foreach (var delay in settings.Delays)
        {
            await SetDelayAsync(delay.Channel, Dg645Channel.T0, 0, ct).ConfigureAwait(false);
        }

        foreach (var delay in OrderByReference(settings.Delays))
        {
            await SetDelayAsync(delay.Channel, delay.Reference, delay.DelaySeconds, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A setting the firmware may not have: its value, or <paramref name="fallback"/>.</summary>
    private static async Task<T> OrDefaultAsync<T>(Task<T> read, T fallback)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (Dg645UnsupportedCommandException)
        {
            return fallback;
        }
    }

    private static async Task SkipIfUnsupportedAsync(Task write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        catch (Dg645UnsupportedCommandException)
        {
            // Not on this firmware; the client has logged it once.
        }
    }

    /// <summary>Orders delays so each channel's reference is set before the channel itself.</summary>
    internal static IEnumerable<ChannelDelaySettings> OrderByReference(IEnumerable<ChannelDelaySettings> delays)
    {
        var pending = delays.ToList();
        var placed = new HashSet<Dg645Channel> { Dg645Channel.T0, Dg645Channel.T1 };
        var pendingChannels = pending.Select(d => d.Channel).ToHashSet();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(d => placed.Contains(d.Reference) || !pendingChannels.Contains(d.Reference))
                        ?? pending[0]; // a cycle in the file; let the instrument report it
            pending.Remove(ready);
            pendingChannels.Remove(ready.Channel);
            placed.Add(ready.Channel);
            yield return ready;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------------------------

    private static void EnsureOutputPrescaler(PrescaleInput output)
    {
        if (output == PrescaleInput.Trigger)
        {
            throw new ArgumentOutOfRangeException(nameof(output), "Prescale phase applies to outputs AB, CD, EF and GH only.");
        }
    }

    private async Task<int> QueryIntAsync(string query, CancellationToken ct) =>
        F.ParseInt(await QueryAsync(query, ct: ct).ConfigureAwait(false));

    private async Task<long> QueryLongAsync(string query, CancellationToken ct) =>
        F.ParseLong(await QueryAsync(query, ct: ct).ConfigureAwait(false));

    private async Task<double> QueryDoubleAsync(string query, CancellationToken ct) =>
        F.ParseDouble(await QueryAsync(query, ct: ct).ConfigureAwait(false));

    private async Task<string[]> QueryPartsAsync(string query, int count, CancellationToken ct)
    {
        var response = await QueryAsync(query, ct: ct).ConfigureAwait(false);
        var parts = response.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != count)
        {
            throw new FormatException($"Unexpected reply to '{query}': '{response}'.");
        }

        return parts;
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!transport.IsConnected)
            {
                throw new InvalidOperationException("The instrument is not connected.");
            }

            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunAsync(Func<Task> operation, CancellationToken ct) =>
        RunAsync(async () =>
        {
            await operation().ConfigureAwait(false);
            return true;
        }, ct);

    private async Task WriteCoreAsync(string command, CancellationToken ct)
    {
        Log(CommunicationDirection.Sent, command);
        if (!AuditedChange.Value && IsSetCommand(command))
        {
            UnrecordedCommandSent?.Invoke(this, command);
        }

        await transport.WriteLineAsync(command, ct).ConfigureAwait(false);
    }

    private async Task<string> ReadCoreAsync(TimeSpan timeout, CancellationToken ct)
    {
        var line = await transport.ReadLineAsync(timeout, ct).ConfigureAwait(false);
        Log(CommunicationDirection.Received, line);
        return line;
    }

    /// <summary>
    /// Sends a query and reads the reply. With <paramref name="diagnose"/>, a query that gets no reply is followed by
    /// LERR?: the DG645 says nothing to a command its firmware does not have, but records error 111. Reading the
    /// error here also stops it being blamed on the next command. Background polling (see
    /// <see cref="SuppressTrafficLogging"/>) skips this, so a lost connection is still noticed quickly.
    /// </summary>
    private async Task<string> QueryCoreAsync(string query, TimeSpan timeout, CancellationToken ct, bool diagnose = true)
    {
        var stale = await transport.DrainAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        if (stale.Length > 0)
        {
            Log(CommunicationDirection.Info, $"Discarded unexpected data: {stale.Trim()}");
        }

        await WriteCoreAsync(query, ct).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A late reply would otherwise be mistaken for the answer to the next query.
            var late = await transport.DrainAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            if (late.Length > 0)
            {
                Log(CommunicationDirection.Info, $"Discarded late reply: {late.Trim()}");
            }

            var answering = false;
            if (diagnose && !QuietTraffic.Value)
            {
                try
                {
                    var errors = await ReadErrorQueueCoreAsync(ct).ConfigureAwait(false);
                    answering = true;
                    if (errors.Contains(UndefinedCommandError))
                    {
                        MarkUnsupported(query);
                        throw new Dg645UnsupportedCommandException(query);
                    }
                }
                catch (TimeoutException)
                {
                    // No reply to LERR? either: the connection is the problem, reported below.
                }
            }

            var message = answering
                ? $"No reply to '{query}' within {timeout.TotalSeconds:0.#} s, although the instrument is answering."
                : $"No reply to '{query}' within {timeout.TotalSeconds:0.#} s. The connection may have been lost.";
            Log(CommunicationDirection.Error, message);
            throw new TimeoutException(message);
        }
    }

    private async Task<IReadOnlyList<int>> ReadErrorQueueCoreAsync(CancellationToken ct)
    {
        var errors = new List<int>();

        // The DG645 buffers up to 20 errors; stop a little past that in case of a misbehaving link.
        for (var i = 0; i < 25; i++)
        {
            var code = F.ParseInt(await QueryCoreAsync("LERR?", QueryTimeout, ct, diagnose: false).ConfigureAwait(false));
            if (code == 0)
            {
                break;
            }

            errors.Add(code);
            Log(CommunicationDirection.Error, $"Instrument error {code}: {Dg645Names.DescribeError(code)}");
        }

        return errors;
    }

    /// <summary>
    /// Within the returned scope, commands and replies on this async flow are not reported through
    /// <see cref="Communication"/> (errors still are). Used for background polling so the log stays readable.
    /// </summary>
    public IDisposable SuppressTrafficLogging()
    {
        var previous = QuietTraffic.Value;
        QuietTraffic.Value = true;
        return new QuietScope(previous);
    }

    /// <summary>
    /// Within the returned scope, commands sent on this async flow belong to a change the caller records in the
    /// audit trail itself, so <see cref="UnrecordedCommandSent"/> is not raised for them.
    /// </summary>
    public IDisposable BeginAuditedChange()
    {
        var previous = AuditedChange.Value;
        AuditedChange.Value = true;
        return new FlagScope(AuditedChange, previous);
    }

    /// <summary>True if any ';'-separated part of the line is not a query, so sending it can change the instrument.</summary>
    public static bool IsSetCommand(string line) =>
        line.Split(';').Any(part => part.Trim().Length > 0 && !part.Contains('?'));

    private void Log(CommunicationDirection direction, string text)
    {
        if (QuietTraffic.Value && direction is CommunicationDirection.Sent or CommunicationDirection.Received)
        {
            return;
        }

        Communication?.Invoke(this, new CommunicationEventArgs(direction, text));
    }

    private sealed class QuietScope(bool previous) : IDisposable
    {
        public void Dispose() => QuietTraffic.Value = previous;
    }

    private sealed class FlagScope(AsyncLocal<bool> flag, bool previous) : IDisposable
    {
        public void Dispose() => flag.Value = previous;
    }
}
