using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StanfordController.Presentation.Services;

/// <summary>
/// Hands one document to the web browser on this computer, so that it can be opened in the default browser: a web
/// address always opens there, whatever program the document's file type belongs to. Only this computer can connect
/// (127.0.0.1), and only the one document is served.
/// </summary>
/// <remarks>
/// A file in the temporary folder would not do: some browsers cannot see that folder (Firefox installed as a snap on
/// Ubuntu has one of its own), and a file:// link opens in the PDF viewer rather than the browser on many systems.
/// </remarks>
public sealed class LocalDocumentServer : IDisposable
{
    private const int MaxRequestHeadBytes = 16 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string _fileName;
    private readonly byte[] _document;
    private readonly string _contentType;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    /// <exception cref="SocketException">This computer would not let the program listen on 127.0.0.1.</exception>
    public LocalDocumentServer(string fileName, byte[] document, string contentType)
    {
        _fileName = fileName;
        _document = document;
        _contentType = contentType;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Address = new Uri($"http://127.0.0.1:{Port}/{Uri.EscapeDataString(fileName)}");
        _ = AcceptAsync();
    }

    public int Port { get; }

    /// <summary>The document's address, e.g. http://127.0.0.1:52814/Tempo-Quick-Reference.pdf.</summary>
    public Uri Address { get; }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(RequestTimeout);
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, timeout.Token);
                await RespondAsync(stream, head, timeout.Token);
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The browser went away or never finished asking; there is no one to answer.
            }
        }
    }

    /// <summary>The request line and headers, or null if the request is too long or ends early.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, end);
            }
        }

        return null;
    }

    private (int Status, string Reason) Check(string? head)
    {
        if (head is null)
        {
            return (400, "Bad Request");
        }

        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return (400, "Bad Request");
        }

        // Only the addresses of this computer: a web page that points its own name at 127.0.0.1 gets nothing.
        var host = lines.Skip(1)
            .Select(line => line.Split(':', 2))
            .Where(header => header.Length == 2 && header[0].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
            .Select(header => header[1].Trim())
            .FirstOrDefault();
        if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}")
        {
            return (403, "Forbidden");
        }

        if (request[0] is not ("GET" or "HEAD"))
        {
            return (405, "Method Not Allowed");
        }

        return request[1].Split('?', 2)[0] == Address.AbsolutePath ? (200, "OK") : (404, "Not Found");
    }

    private async Task RespondAsync(NetworkStream stream, string? head, CancellationToken cancellationToken)
    {
        var (status, reason) = Check(head);
        var found = status == 200;
        var body = found ? _document : Encoding.UTF8.GetBytes(reason);
        var headers = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append($"Content-Type: {(found ? _contentType : "text/plain; charset=utf-8")}\r\n")
            .Append($"Content-Length: {body.Length}\r\n");
        if (found)
        {
            headers.Append($"Content-Disposition: inline; filename=\"{_fileName}\"\r\n");
        }
        else if (status == 405)
        {
            headers.Append("Allow: GET, HEAD\r\n");
        }

        headers.Append("X-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), cancellationToken);
        if (head?.StartsWith("HEAD ", StringComparison.Ordinal) != true)
        {
            await stream.WriteAsync(body, cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
    }
}
