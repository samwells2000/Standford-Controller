using System.Net;
using System.Net.Sockets;
using System.Text;
using StanfordController.Core;
using StanfordController.Core.Discovery;
using StanfordController.Core.Protocol;
using StanfordController.Core.Simulation;
using StanfordController.Core.Transport;

namespace StanfordController.Tests;

/// <summary>A loopback TCP server that speaks like a DG645 bare socket, with optional delays and fragmentation.</summary>
internal sealed class FakeInstrumentServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeInstrumentServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public Dg645Simulator Simulator { get; } = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Delay before answering a given query, to provoke client timeouts.</summary>
    public Dictionary<string, TimeSpan> ReplyDelays { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Send replies a few bytes at a time.</summary>
    public bool Fragment { get; set; }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            try
            {
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (ReplyDelays.TryGetValue(line.Trim(), out var delay))
                    {
                        await Task.Delay(delay, _stop.Token);
                    }

                    foreach (var reply in Simulator.Process(line))
                    {
                        var bytes = Encoding.ASCII.GetBytes(reply + "\r\n");
                        if (Fragment)
                        {
                            foreach (var chunk in bytes.Chunk(3))
                            {
                                await stream.WriteAsync(chunk, _stop.Token);
                                await stream.FlushAsync(_stop.Token);
                                await Task.Delay(2, _stop.Token);
                            }
                        }
                        else
                        {
                            await stream.WriteAsync(bytes, _stop.Token);
                        }
                    }
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch
        {
        }
    }
}

public class TcpTransportTests
{
    [Fact]
    public async Task ClientWorksOverARealSocketWithFragmentedReplies()
    {
        await using var server = new FakeInstrumentServer { Fragment = true };
        var client = new Dg645Client(new TcpTransport("127.0.0.1", server.Port));
        await client.ConnectAsync();

        Assert.Contains("DG645", client.Identity);
        await client.SetDelayAsync(Dg645Channel.D, Dg645Channel.C, 42e-9);
        Assert.Equal((Dg645Channel.C, 42e-9), await client.GetDelayAsync(Dg645Channel.D));

        await client.DisconnectAsync();
        Assert.Equal("LCAL", server.Simulator.ReceivedLines[^1]);
    }

    [Fact]
    public async Task LateReplyIsDiscardedInsteadOfAnsweringTheNextQuery()
    {
        await using var server = new FakeInstrumentServer();
        server.ReplyDelays["TRAT?"] = TimeSpan.FromMilliseconds(400);
        var client = new Dg645Client(new TcpTransport("127.0.0.1", server.Port)) { QueryTimeout = TimeSpan.FromMilliseconds(150) };
        await client.ConnectAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => client.GetTriggerRateAsync());
        await Task.Delay(400);

        // The stale "+10000.000000" must not be returned here.
        Assert.Equal(10, await client.GetBurstCountAsync());
    }

    [Fact]
    public async Task ConnectingToAClosedPortFails()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var client = new Dg645Client(new TcpTransport("127.0.0.1", port, TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task ScannerProbeReadsTheIdentity()
    {
        await using var server = new FakeInstrumentServer();
        var found = await LanScanner.ScanAsync([IPAddress.Loopback], server.Port, TimeSpan.FromSeconds(1));
        var instrument = Assert.Single(found);
        Assert.True(instrument.IsDg645);
        Assert.Equal("DG645", instrument.Model);
        Assert.Equal("s/n000000", instrument.SerialNumber);
    }

    [Fact]
    public void SubnetEnumerationSkipsNetworkBroadcastAndSelf()
    {
        var hosts = LanScanner.EnumerateHosts(new LocalNetwork("eth0", IPAddress.Parse("192.168.1.10"), 24)).ToList();
        Assert.Equal(253, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), hosts[^1]);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.10"), hosts);
    }

    [Fact]
    public void LargeSubnetsAreNarrowedToTheLocal24()
    {
        var hosts = LanScanner.EnumerateHosts(new LocalNetwork("eth0", IPAddress.Parse("10.20.30.40"), 8)).ToList();
        Assert.Equal(253, hosts.Count);
        Assert.All(hosts, h => Assert.StartsWith("10.20.30.", h.ToString()));
    }
}
