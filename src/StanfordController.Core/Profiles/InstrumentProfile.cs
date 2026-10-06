using System.Text.Json.Serialization;
using StanfordController.Core.Transport;

namespace StanfordController.Core.Profiles;

/// <summary>What to do with a saved setup when its instrument connects.</summary>
public enum SetupOnConnect
{
    /// <summary>Ask whether to keep the instrument's settings or load the saved setup (only when they differ).</summary>
    Ask,

    /// <summary>Always load the saved setup.</summary>
    Restore,

    /// <summary>Always keep the settings already on the instrument.</summary>
    KeepInstrument,
}

/// <summary>A saved instrument: a friendly name plus everything needed to reach it.</summary>
public sealed class InstrumentProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The name shown in the instrument list, e.g. "Laser lab DG645".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>IP address or host name.</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = TcpTransport.DefaultPort;

    public int TimeoutMs { get; set; } = 3000;

    public string? Location { get; set; }

    public string? Notes { get; set; }

    /// <summary>Connects to the built-in simulator instead of the network.</summary>
    public bool IsSimulator { get; set; }

    /// <summary>Send LCAL on disconnect so the front panel is usable again.</summary>
    public bool ReturnToLocalOnDisconnect { get; set; } = true;

    /// <summary>Connect as soon as the configuration is opened (including when the program starts).</summary>
    public bool ConnectOnStartup { get; set; }

    /// <summary>
    /// The instrument's complete setup (delays, outputs, trigger, burst) as last saved on this computer,
    /// so it can be put back without re-entering anything. Null until a setup has been saved.
    /// </summary>
    public Dg645Settings? StoredSetup { get; set; }

    /// <summary>What to do when the instrument connects and its settings differ from <see cref="StoredSetup"/>.</summary>
    public SetupOnConnect SetupOnConnect { get; set; } = SetupOnConnect.Ask;

    public string? LastKnownIdentity { get; set; }

    public DateTime? LastConnectedUtc { get; set; }

    [JsonIgnore]
    public string Address => IsSimulator ? "Simulated instrument" : Port == TcpTransport.DefaultPort ? Host : $"{Host}:{Port}";

    public InstrumentProfile Clone() => (InstrumentProfile)MemberwiseClone();

    /// <summary>"192.168.1.300" or "10.0.1" would otherwise pass as DNS names and fail only when connecting.</summary>
    private static bool LooksLikeBadIPv4(string host) =>
        host.All(c => char.IsDigit(c) || c == '.')
        && !(host.Count(c => c == '.') == 3 && System.Net.IPAddress.TryParse(host, out _));

    public IDg645Transport CreateTransport() => IsSimulator
        ? new SimulatorTransport()
        : new TcpTransport(Host, Port, TimeSpan.FromMilliseconds(Math.Max(TimeoutMs, 500)));

    public Dg645Client CreateClient() => new(CreateTransport())
    {
        QueryTimeout = TimeSpan.FromMilliseconds(Math.Max(TimeoutMs, 200)),
    };

    /// <summary>Returns a list of problems, or an empty list if the profile can be saved.</summary>
    public IReadOnlyList<string> Validate(IEnumerable<InstrumentProfile> others)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("Enter a friendly name.");
        }
        else if (others.Any(o => o.Id != Id && string.Equals(o.Name.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"Another instrument is already called \"{Name.Trim()}\".");
        }

        if (!IsSimulator)
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                problems.Add("Enter the instrument's IP address or host name.");
            }
            else if (Uri.CheckHostName(Host.Trim()) == UriHostNameType.Unknown || LooksLikeBadIPv4(Host.Trim()))
            {
                problems.Add($"\"{Host.Trim()}\" is not a valid IP address or host name.");
            }

            if (Port is < 1 or > 65535)
            {
                problems.Add("The port must be between 1 and 65535 (the DG645 uses 5025).");
            }
        }

        if (TimeoutMs is < 200 or > 60000)
        {
            problems.Add("The timeout must be between 200 and 60000 ms.");
        }

        return problems;
    }
}
