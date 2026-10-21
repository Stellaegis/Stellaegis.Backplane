using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.Client;

/// <summary>
/// The options of a host reaching the hub, bound from <c>Stellaegis:Backplane:Client</c>: where the hub is, which
/// recipients the host serves, and how the client behaves when the hub is away.
/// <code>
/// Stellaegis:Backplane:Client:BaseUrl = https://backplane.internal
/// Stellaegis:Backplane:Client:ServiceName = identity
/// Stellaegis:Backplane:Client:Compartments:0 = acme
/// Stellaegis:Backplane:Client:Compartments:1 = globex
/// </code>
/// </summary>
public sealed class BackplaneClientOptions : StellaegisClientOptions
{
    /// <summary>The path of the hub under <see cref="StellaegisClientOptions.BaseUrl"/>. <c>/backplane/hub</c> unless set.</summary>
    public string HubPath { get; set; } = "/backplane/hub";

    /// <summary>The path of the HTTP routes under <see cref="StellaegisClientOptions.BaseUrl"/>. <c>/backplane</c> unless set.</summary>
    public string EndpointsPath { get; set; } = "/backplane";

    /// <summary>The name of this service, an identifier, to receive the events addressed to it. None unless set.</summary>
    public string? ServiceName { get; set; }

    /// <summary>The compartments this host serves, to receive their events. None unless set.</summary>
    public string[] Compartments { get; set; } = [];

    /// <summary>How many events wait for the hub before the oldest is dropped. 500 unless set.</summary>
    public int QueueCapacity { get; set; } = 500;

    /// <summary>How long the client waits before reconnecting, and before retrying a publication. Five seconds unless set.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether a reconnection replays, from the journal of the hub, what was published meanwhile. True unless set.</summary>
    public bool ReplayOnReconnect { get; set; } = true;

    /// <summary>How many times the host tries to connect at startup before refusing to start. 10 unless set.</summary>
    public int StartupAttempts { get; set; } = 10;

    /// <summary>The recipients this host serves, as the options name them.</summary>
    public IReadOnlyList<Recipient> Recipients()
    {
        var recipients = new List<Recipient>(Compartments.Select(compartment => Recipient.Of(Compartment.Parse(compartment))));
        if (ServiceName is not null)
            recipients.Add(Recipient.Service(ServiceName));
        return recipients;
    }

    /// <inheritdoc />
    public override string? FindError()
    {
        if (base.FindError() is { } error)
            return error;
        if (!HubPath.StartsWith('/'))
            return "HubPath must start with a slash.";
        if (!EndpointsPath.StartsWith('/'))
            return "EndpointsPath must start with a slash.";
        if (ServiceName is not null && !NameRules.IsIdentifier(ServiceName))
            return $"ServiceName must be an identifier: {NameRules.IdentifierRule}.";
        if (Compartments.FirstOrDefault(compartment => !Compartment.TryParse(compartment, out _)) is { } invalid)
            return $"Compartments: '{invalid}' is not a compartment.";
        if (QueueCapacity <= 0)
            return "QueueCapacity must be positive.";
        if (RetryDelay <= TimeSpan.Zero)
            return "RetryDelay must be positive.";
        if (StartupAttempts <= 0)
            return "StartupAttempts must be positive.";
        return null;
    }
}

/// <summary>
/// Gives the client the bearer token it presents to the hub and to the HTTP routes: the host implements it over
/// its own way of obtaining one — client credentials, a static token. Without one registered, the client presents
/// no token, for a host that authenticates otherwise.
/// </summary>
public interface IBackplaneTokenProvider
{
    /// <summary>The token to present, or <see langword="null"/> for none.</summary>
    ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default);
}
