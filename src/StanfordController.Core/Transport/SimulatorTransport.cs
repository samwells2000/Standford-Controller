using System.Threading.Channels;
using StanfordController.Core.Simulation;

namespace StanfordController.Core.Transport;

/// <summary>In-process transport backed by <see cref="Dg645Simulator"/>, used for the demo instrument and tests.</summary>
public sealed class SimulatorTransport(Dg645Simulator simulator) : IDg645Transport
{
    private Channel<string> _responses = Channel.CreateUnbounded<string>();
    private bool _connected;

    public SimulatorTransport() : this(new Dg645Simulator())
    {
    }

    public Dg645Simulator Simulator { get; } = simulator;

    public string Description => "Simulated DG645";

    public bool IsConnected => _connected;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _responses = Channel.CreateUnbounded<string>();
        _connected = true;
        return Task.CompletedTask;
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Not connected.");
        }

        foreach (var response in Simulator.Process(line))
        {
            _responses.Writer.TryWrite(response);
        }

        return Task.CompletedTask;
    }

    public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_responses.Reader.TryRead(out var ready))
        {
            return ready;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await _responses.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("No response from the instrument.");
        }
    }

    public Task<string> DrainAsync(TimeSpan quietPeriod, CancellationToken cancellationToken)
    {
        var discarded = new List<string>();
        while (_responses.Reader.TryRead(out var line))
        {
            discarded.Add(line);
        }

        return Task.FromResult(string.Join("\n", discarded));
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }
}
