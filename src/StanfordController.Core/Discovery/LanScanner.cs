using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using StanfordController.Core.Transport;

namespace StanfordController.Core.Discovery;

/// <summary>An instrument that answered *IDN? during a scan.</summary>
public sealed record DiscoveredInstrument(string Address, int Port, string Identity)
{
    public bool IsDg645 => Identity.Contains("DG645", StringComparison.OrdinalIgnoreCase);

    public string Model => Identity.Split(',') is { Length: >= 2 } parts ? parts[1].Trim() : Identity;

    public string SerialNumber => Identity.Split(',') is { Length: >= 3 } parts ? parts[2].Trim() : string.Empty;
}

/// <summary>A local IPv4 network the PC is attached to.</summary>
public sealed record LocalNetwork(string InterfaceName, IPAddress Address, int PrefixLength)
{
    public override string ToString() => $"{InterfaceName} — {Address}/{PrefixLength}";
}

/// <summary>Finds instruments by probing every host on the PC's local subnets for an open SCPI port.</summary>
public static class LanScanner
{
    /// <summary>Subnets larger than this are narrowed to the /24 around the PC's own address.</summary>
    public const int MaxHostsPerNetwork = 1022;

    public static IReadOnlyList<LocalNetwork> GetLocalNetworks()
    {
        var networks = new List<LocalNetwork>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                {
                    networks.Add(new LocalNetwork(nic.Name, unicast.Address, unicast.PrefixLength));
                }
            }
        }

        return networks;
    }

    /// <summary>Every host address on the network except the PC itself.</summary>
    public static IEnumerable<IPAddress> EnumerateHosts(LocalNetwork network)
    {
        var prefix = Math.Clamp(network.PrefixLength, 0, 32);
        var hostCount = prefix >= 31 ? 0 : (1L << (32 - prefix)) - 2;
        if (hostCount > MaxHostsPerNetwork)
        {
            prefix = 24;
        }

        if (prefix >= 31)
        {
            yield break;
        }

        var self = ToUInt32(network.Address);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var first = (self & mask) + 1;
        var last = (self | ~mask) - 1;
        for (var host = first; host <= last && host != 0; host++)
        {
            if (host != self)
            {
                yield return FromUInt32(host);
            }
        }
    }

    public static async Task<IReadOnlyList<DiscoveredInstrument>> ScanAsync(
        IEnumerable<IPAddress> hosts,
        int port = TcpTransport.DefaultPort,
        TimeSpan? connectTimeout = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var targets = hosts.Distinct().ToList();
        var found = new List<DiscoveredInstrument>();
        var done = 0;
        var timeout = connectTimeout ?? TimeSpan.FromMilliseconds(400);

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = cancellationToken },
            async (host, ct) =>
            {
                var result = await ProbeAsync(host, port, timeout, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    lock (found)
                    {
                        found.Add(result);
                    }
                }

                progress?.Report((Interlocked.Increment(ref done), targets.Count));
            }).ConfigureAwait(false);

        return found
            .OrderByDescending(f => f.IsDg645)
            .ThenBy(f => ToUInt32(IPAddress.Parse(f.Address)))
            .ToList();
    }

    /// <summary>Connects to one host and asks for its identity; null if nothing answers.</summary>
    public static async Task<DiscoveredInstrument?> ProbeAsync(IPAddress host, int port, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        var transport = new TcpTransport(host.ToString(), port, connectTimeout);
        try
        {
            await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await transport.WriteLineAsync("*IDN?", cancellationToken).ConfigureAwait(false);
            var identity = await transport.ReadLineAsync(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(identity) ? null : new DiscoveredInstrument(host.ToString(), port, identity.Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
