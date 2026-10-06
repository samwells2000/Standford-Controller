using System.Net.Sockets;
using System.Text;

namespace StanfordController.Core.Transport;

/// <summary>
/// Raw TCP socket transport. The DG645 serves its "bare socket" interface on port 5025
/// (telnet on 5024 adds option negotiation and is not supported here).
/// </summary>
public sealed class TcpTransport : IDg645Transport
{
    public const int DefaultPort = 5025;

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _connectTimeout;
    private readonly byte[] _buffer = new byte[4096];
    private readonly StringBuilder _received = new();
    private TcpClient? _client;
    private NetworkStream? _stream;

    // A socket read that outlived a timeout is kept and awaited by the next read, so no data is lost
    // and the socket is never left with a cancelled operation.
    private Task<int>? _pendingRead;

    public TcpTransport(string host, int port = DefaultPort, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _host = host.Trim();
        _port = port;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    public string Description => $"{_host}:{_port}";

    public bool IsConnected => _client?.Connected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            throw new InvalidOperationException("The transport is already connected.");
        }

        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        try
        {
            await client.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Could not connect to {Description} within {_connectTimeout.TotalSeconds:0.#} s. Check the IP address, that the instrument's LAN and bare-socket interfaces are enabled, and that no firewall blocks TCP port {_port}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _client = client;
        _stream = client.GetStream();
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        var bytes = Encoding.ASCII.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryTakeLine(out var line))
            {
                return line;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !await ReadChunkAsync(remaining, cancellationToken).ConfigureAwait(false))
            {
                throw new TimeoutException("No response from the instrument.");
            }
        }
    }

    public async Task<string> DrainAsync(TimeSpan quietPeriod, CancellationToken cancellationToken)
    {
        var discarded = new StringBuilder();
        discarded.Append(_received);
        _received.Clear();

        while (_stream is not null)
        {
            // With no quiet period, only take what is already waiting; never block.
            if (quietPeriod == TimeSpan.Zero && _pendingRead is null && !_stream.DataAvailable)
            {
                break;
            }

            if (!await ReadChunkAsync(quietPeriod, cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            discarded.Append(_received);
            _received.Clear();
        }

        return discarded.ToString();
    }

    private bool TryTakeLine(out string line)
    {
        for (var i = 0; i < _received.Length; i++)
        {
            if (_received[i] == '\n')
            {
                line = _received.ToString(0, i).TrimEnd('\r');
                _received.Remove(0, i + 1);
                return true;
            }
        }

        line = string.Empty;
        return false;
    }

    /// <summary>Appends the next chunk of received data to the buffer; false if nothing arrived within <paramref name="timeout"/>.</summary>
    private async Task<bool> ReadChunkAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        _pendingRead ??= stream.ReadAsync(_buffer, 0, _buffer.Length, CancellationToken.None);

        if (!_pendingRead.IsCompleted)
        {
            using var delayCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, delayCancel.Token);
            var finished = await Task.WhenAny(_pendingRead, delay).ConfigureAwait(false);
            delayCancel.Cancel();
            if (finished != _pendingRead)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }

        var read = _pendingRead;
        _pendingRead = null;
        var count = await read.ConfigureAwait(false);
        if (count == 0)
        {
            throw new IOException("The instrument closed the connection.");
        }

        _received.Append(Encoding.ASCII.GetString(_buffer, 0, count));
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        var client = _client;
        _client = null;
        _stream = null;
        client?.Dispose();

        if (_pendingRead is { } pending)
        {
            _pendingRead = null;
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch
            {
                // The socket was closed under the read; nothing to report.
            }
        }
    }
}
