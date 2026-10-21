using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane.Server;

/// <summary>
/// What the hub and the HTTP endpoint of publication share: stamping an event with the identity of the service
/// that publishes it and the hub's clock, journaling it when the host registered a journal, and relaying it to
/// the connections of its recipient.
/// </summary>
/// <remarks>
/// The identity is read from the claims of the caller, in this order: <c>client_id</c>, the identifier of a
/// service authenticated with client credentials; <see cref="ClaimTypes.NameIdentifier"/>; <c>sub</c>. A caller
/// with none is refused: an event always says which service published it, and the hub never writes
/// <c>system</c> in its place.
/// </remarks>
public sealed class EventRelay(IHubContext<BackplaneHub> hub, IServiceProvider services, ILogger<EventRelay> logger)
{
    private static readonly string[] IdentityClaims = ["client_id", ClaimTypes.NameIdentifier, "sub"];

    private readonly IHubContext<BackplaneHub> _hub = Guard.NotNull(hub);
    private readonly IEventJournal? _journal = Guard.NotNull(services).GetService(typeof(IEventJournal)) as IEventJournal;
    private readonly ILogger<EventRelay> _logger = Guard.NotNull(logger);

    /// <summary>Whether the host registered a journal, so that replay answers.</summary>
    public bool HasJournal => _journal is not null;

    /// <summary>
    /// Stamps <paramref name="envelope"/> with the identity of <paramref name="user"/> and the moment, journals
    /// it, and relays it to its recipient. Returns the envelope as relayed.
    /// </summary>
    /// <exception cref="UserFacingException">
    /// <c>Event.Malformed</c>: the name or the recipient does not read; <c>Emitter.Unknown</c>: the caller carries no identity.
    /// </exception>
    public async Task<Envelope> RelayAsync(Envelope envelope, ClaimsPrincipal? user, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(envelope);
        if (!BackplaneBuilder.IsEventName(envelope.Name))
            throw new UserFacingException(new Error("Event.Malformed", $"'{envelope.Name}' is not an event name.", ErrorType.Validation));
        if (!Recipient.TryParse(envelope.Recipient, out var recipient))
            throw new UserFacingException(new Error("Event.Malformed", $"'{envelope.Recipient}' is not a recipient.", ErrorType.Validation));
        var emitter = EmitterOf(user)
            ?? throw new UserFacingException(new Error("Emitter.Unknown", "The caller carries no identity to stamp the event with.", ErrorType.Unauthorized));

        var stamped = envelope.Stamped(emitter, DateTimeOffset.UtcNow);
        if (_journal is not null)
        {
            try
            {
                await _journal.AppendAsync(stamped, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "The event {Name} could not be journaled; it is relayed and will not replay.", stamped.Name);
            }
        }

        var clients = recipient.IsAll ? _hub.Clients.All : _hub.Clients.Group(recipient.Segment);
        await clients.SendAsync(BackplaneHub.ReceiveMethod, stamped, cancellationToken).ConfigureAwait(false);
        return stamped;
    }

    /// <summary>The identity of <paramref name="user"/> as the hub stamps it, or <see langword="null"/>.</summary>
    public static Actor? EmitterOf(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return null;
        foreach (var type in IdentityClaims)
        {
            if (user.FindFirst(type)?.Value is { } value && Actor.TryParse(value, out var actor))
                return actor;
        }
        return null;
    }
}
