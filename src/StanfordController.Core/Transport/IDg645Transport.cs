namespace StanfordController.Core.Transport;

/// <summary>
/// A line-oriented link to an instrument. Commands are terminated with LF; responses end with CR LF.
/// Implementations are not thread safe; <see cref="Dg645Client"/> serialises access.
/// </summary>
public interface IDg645Transport : IAsyncDisposable
{
    /// <summary>Human readable endpoint, e.g. "192.168.1.20:5025".</summary>
    string Description { get; }

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>Reads one response line without its terminator. Throws <see cref="TimeoutException"/> if none arrives in time.</summary>
    Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Discards anything already received. When <paramref name="quietPeriod"/> is non-zero, keeps discarding
    /// until nothing has arrived for that long (used to resynchronise after a late reply).
    /// </summary>
    /// <returns>The discarded text, or an empty string.</returns>
    Task<string> DrainAsync(TimeSpan quietPeriod, CancellationToken cancellationToken);
}
