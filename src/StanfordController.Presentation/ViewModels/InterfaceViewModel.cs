using System.Windows.Input;
using StanfordController.Core.Audit;
using StanfordController.Core.Protocol;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>RS-232, GPIB and LAN configuration (IFCF/IFRS/EMAC), plus local/remote and lock control.</summary>
public sealed class InterfaceViewModel : ParameterViewModel
{
    private string _macAddress = "—";
    private string? _lockStatus;

    public InterfaceViewModel(IInstrumentContext context)
        : base(context, "Interfaces")
    {
        Rs232Enabled = Toggle(InterfaceSetting.Rs232Enable);
        BaudRate = new ChoiceParameterViewModel(context, Dg645Names.Describe(InterfaceSetting.Rs232BaudRate),
            Dg645Names.BaudRates.Select(b => new ChoiceOption(b, $"{b} baud")).ToList(),
            async ct => int.Parse(await Client.GetInterfaceSettingAsync(InterfaceSetting.Rs232BaudRate, ct), System.Globalization.CultureInfo.InvariantCulture),
            (v, ct) => Client.SetInterfaceSettingAsync(InterfaceSetting.Rs232BaudRate, v.ToString(System.Globalization.CultureInfo.InvariantCulture), ct))
        {
            Hint = "IFCF 1,baud. Takes effect after the RS-232 interface is reset.",
        };
        GpibEnabled = Toggle(InterfaceSetting.GpibEnable);
        GpibAddress = Param.Integer(context, Dg645Names.Describe(InterfaceSetting.GpibAddress),
            async ct => Core.Protocol.Dg645Format.ParseLong(await Client.GetInterfaceSettingAsync(InterfaceSetting.GpibAddress, ct)),
            (v, ct) => Client.SetInterfaceSettingAsync(InterfaceSetting.GpibAddress, v.ToString(System.Globalization.CultureInfo.InvariantCulture), ct),
            min: 0, max: 30, hint: "IFCF 3,addr. Takes effect after the GPIB interface is reset.");

        LanEnabled = Toggle(InterfaceSetting.LanEnable, confirmOff: "Disabling the LAN interface will stop this program from reaching the instrument after the next LAN reset or power cycle. Continue?");
        DhcpEnabled = Toggle(InterfaceSetting.DhcpEnable);
        AutoIpEnabled = Toggle(InterfaceSetting.AutoIpEnable);
        StaticIpEnabled = Toggle(InterfaceSetting.StaticIpEnable);
        BareSocketEnabled = Toggle(InterfaceSetting.BareSocketEnable, confirmOff: "This program talks to the instrument through the bare socket (port 5025). Disabling it will stop this program from connecting after the next LAN reset or power cycle. Continue?");
        TelnetEnabled = Toggle(InterfaceSetting.TelnetEnable);
        Vxi11Enabled = Toggle(InterfaceSetting.Vxi11Enable);
        StaticIpAddress = Ip(InterfaceSetting.StaticIpAddress);
        SubnetMask = Ip(InterfaceSetting.SubnetMask);
        DefaultGateway = Ip(InterfaceSetting.DefaultGateway);

        ResetRs232Command = new AsyncRelayCommand(() => ResetAsync(InterfaceKind.Rs232));
        ResetGpibCommand = new AsyncRelayCommand(() => ResetAsync(InterfaceKind.Gpib));
        ResetLanCommand = new AsyncRelayCommand(() => ResetAsync(InterfaceKind.Lan));
        GoToLocalCommand = new AsyncRelayCommand(() => ActAsync(() => Client.GoToLocalAsync(), AuditActions.LocalControl, details: () => "Front panel returned to local control"));
        GoToRemoteCommand = new AsyncRelayCommand(() => ActAsync(() => Client.GoToRemoteAsync(), AuditActions.RemoteControl, details: () => "Front panel locked for remote control"));
        RequestLockCommand = new AsyncRelayCommand(() => ActAsync(async () =>
            LockStatus = await Client.RequestLockAsync() ? "Lock granted: other interfaces cannot change settings." : "Lock refused: another interface holds it.",
            AuditActions.Lock, details: () => LockStatus));
        ReleaseLockCommand = new AsyncRelayCommand(() => ActAsync(async () =>
            LockStatus = await Client.ReleaseLockAsync() ? "Lock released." : "This connection did not hold the lock.",
            AuditActions.Lock, details: () => LockStatus));
    }

    public ToggleParameterViewModel Rs232Enabled { get; }

    public ChoiceParameterViewModel BaudRate { get; }

    public ToggleParameterViewModel GpibEnabled { get; }

    public EditableParameterViewModel<long> GpibAddress { get; }

    public ToggleParameterViewModel LanEnabled { get; }

    public ToggleParameterViewModel DhcpEnabled { get; }

    public ToggleParameterViewModel AutoIpEnabled { get; }

    public ToggleParameterViewModel StaticIpEnabled { get; }

    public ToggleParameterViewModel BareSocketEnabled { get; }

    public ToggleParameterViewModel TelnetEnabled { get; }

    public ToggleParameterViewModel Vxi11Enabled { get; }

    public EditableParameterViewModel<string> StaticIpAddress { get; }

    public EditableParameterViewModel<string> SubnetMask { get; }

    public EditableParameterViewModel<string> DefaultGateway { get; }

    public string MacAddress
    {
        get => _macAddress;
        private set => SetProperty(ref _macAddress, value);
    }

    public string? LockStatus
    {
        get => _lockStatus;
        private set => SetProperty(ref _lockStatus, value);
    }

    public ICommand ResetRs232Command { get; }

    public ICommand ResetGpibCommand { get; }

    public ICommand ResetLanCommand { get; }

    public ICommand GoToLocalCommand { get; }

    public ICommand GoToRemoteCommand { get; }

    public ICommand RequestLockCommand { get; }

    public ICommand ReleaseLockCommand { get; }

    private IEnumerable<ParameterViewModel> Parameters =>
    [
        Rs232Enabled, BaudRate, GpibEnabled, GpibAddress, LanEnabled, DhcpEnabled, AutoIpEnabled, StaticIpEnabled,
        BareSocketEnabled, TelnetEnabled, Vxi11Enabled, StaticIpAddress, SubnetMask, DefaultGateway,
    ];

    public override async Task RefreshAsync()
    {
        foreach (var parameter in Parameters)
        {
            await parameter.RefreshAsync();
        }

        await RunAsync(async () => MacAddress = await Client.GetMacAddressAsync());
    }

    private ToggleParameterViewModel Toggle(InterfaceSetting setting, string? confirmOff = null) =>
        new(Context, Dg645Names.Describe(setting),
            async ct => Core.Protocol.Dg645Format.ParseBool(await Client.GetInterfaceSettingAsync(setting, ct)),
            (v, ct) => Client.SetInterfaceSettingAsync(setting, v ? "1" : "0", ct))
        {
            Hint = $"IFCF {(int)setting},{{0|1}}",
            ConfirmChange = confirmOff is null ? null : async on => on || await Context.Dialogs.ConfirmAsync("Interface settings", confirmOff, "Turn off"),
        };

    private EditableParameterViewModel<string> Ip(InterfaceSetting setting) =>
        Param.IpAddress(Context, Dg645Names.Describe(setting),
            ct => Client.GetInterfaceSettingAsync(setting, ct),
            (v, ct) => Client.SetInterfaceSettingAsync(setting, v, ct),
            hint: $"IFCF {(int)setting},a.b.c.d — used when static IP is enabled; applied after a LAN reset.");

    private async Task ResetAsync(InterfaceKind kind)
    {
        if (kind == InterfaceKind.Lan)
        {
            if (!await Context.Dialogs.ConfirmAsync("Reset LAN interface",
                    "Resetting the LAN interface applies new network settings and closes this connection. " +
                    "If the IP address changes, update the instrument's saved address before reconnecting.\n\nReset now?", "Reset"))
            {
                return;
            }

            if (await ActAsync(() => Client.ResetInterfaceAsync(kind), AuditActions.InterfaceReset, Describe(kind)))
            {
                Context.RequestDisconnect("The LAN interface was reset. Reconnect once the instrument is back on the network.");
            }

            return;
        }

        await ActAsync(() => Client.ResetInterfaceAsync(kind), AuditActions.InterfaceReset, Describe(kind));
    }

    private static string Describe(InterfaceKind kind) => kind switch
    {
        InterfaceKind.Rs232 => "RS-232",
        InterfaceKind.Gpib => "GPIB",
        _ => "LAN",
    };
}
