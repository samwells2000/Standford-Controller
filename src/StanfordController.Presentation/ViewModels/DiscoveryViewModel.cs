using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Windows.Input;
using StanfordController.Core.Discovery;
using StanfordController.Core.Transport;
using StanfordController.Presentation.Mvvm;

namespace StanfordController.Presentation.ViewModels;

/// <summary>The "Find instruments" dialog: scans the local subnets (or one address) for instruments on port 5025.</summary>
public sealed class DiscoveryViewModel : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    private bool _isScanning;
    private double _progress;
    private string _statusText;
    private string _portText = TcpTransport.DefaultPort.ToString(CultureInfo.InvariantCulture);
    private string _probeAddress = string.Empty;
    private DiscoveredInstrument? _selected;

    public DiscoveryViewModel()
    {
        Networks = LanScanner.GetLocalNetworks();
        _statusText = Networks.Count == 0
            ? "No active IPv4 network adapters were found."
            : "Scan the networks below for instruments, or enter an address to check.";
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !_isScanning && Networks.Count > 0);
        ProbeCommand = new AsyncRelayCommand(ProbeAsync, () => !_isScanning && !string.IsNullOrWhiteSpace(_probeAddress));
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => _isScanning);
    }

    public IReadOnlyList<LocalNetwork> Networks { get; }

    public string NetworksText => Networks.Count == 0 ? "None" : string.Join(Environment.NewLine, Networks);

    public ObservableCollection<DiscoveredInstrument> Results { get; } = [];

    public DiscoveredInstrument? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                foreach (var command in new[] { ScanCommand, ProbeCommand, CancelCommand })
                {
                    (command as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
                }
            }
        }
    }

    /// <summary>0 to 1.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string PortText
    {
        get => _portText;
        set => SetProperty(ref _portText, value);
    }

    public string ProbeAddress
    {
        get => _probeAddress;
        set
        {
            if (SetProperty(ref _probeAddress, value))
            {
                (ProbeCommand as IRaiseCanExecuteChanged)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand ScanCommand { get; }

    public ICommand ProbeCommand { get; }

    public ICommand CancelCommand { get; }

    private int Port => int.TryParse(_portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536
        ? port
        : TcpTransport.DefaultPort;

    private async Task ScanAsync()
    {
        var hosts = Networks.SelectMany(LanScanner.EnumerateHosts).Distinct().ToList();
        await RunScanAsync(hosts, $"Scanning {hosts.Count} addresses on port {Port}…");
    }

    private async Task ProbeAsync()
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(_probeAddress.Trim(), out var ip)
                ? [ip]
                : (await Dns.GetHostAddressesAsync(_probeAddress.Trim())).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not resolve \"{_probeAddress.Trim()}\": {ex.Message}";
            return;
        }

        await RunScanAsync(addresses, $"Checking {_probeAddress.Trim()} on port {Port}…", TimeSpan.FromSeconds(3));
    }

    private async Task RunScanAsync(IReadOnlyCollection<IPAddress> hosts, string startText, TimeSpan? timeout = null)
    {
        IsScanning = true;
        Progress = 0;
        StatusText = startText;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total)>(p => Progress = p.Total == 0 ? 1 : (double)p.Done / p.Total);
        try
        {
            var found = await LanScanner.ScanAsync(hosts, Port, timeout, progress, _cancellation.Token);
            foreach (var instrument in found)
            {
                if (!Results.Any(r => r.Address == instrument.Address && r.Port == instrument.Port))
                {
                    Results.Add(instrument);
                }
            }

            var dg645Count = Results.Count(r => r.IsDg645);
            StatusText = Results.Count == 0
                ? "No instruments answered. Check the cable and that the DG645's LAN and bare-socket interfaces are enabled. Directly connected instruments using Auto-IP (169.254.x.x) are best found by entering the address shown on the instrument's front panel."
                : $"Found {dg645Count} DG645{(dg645Count == 1 ? "" : "s")}{(Results.Count > dg645Count ? $" and {Results.Count - dg645Count} other instrument(s)" : "")}. Select one and choose Add.";
            Selected ??= Results.FirstOrDefault(r => r.IsDg645) ?? Results.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
        }
        finally
        {
            IsScanning = false;
            Progress = 1;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }
}
