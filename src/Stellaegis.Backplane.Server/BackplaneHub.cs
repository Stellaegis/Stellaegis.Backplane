using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;

namespace Stellaegis.Backplane.Server;

/// <summary>
/// Decides whether a connection may receive the events of a recipient — the services of a compartment, or a
/// named service. Trusted services on a private network: unless the application registers its own policy, every
/// authenticated connection may join any recipient, and who may connect at all is the authorization the host
/// declares on the hub.
/// </summary>
public interface IBackplaneMembershipPolicy
{
    /// <summary>Whether <paramref name="user"/> may receive the events of <paramref name="recipient"/>.</summary>
    Task<bool> CanJoinAsync(ClaimsPrincipal user, Recipient recipient, CancellationToken cancellationToken = default);
}

/// <summary>The policy of a backplane among trusted services: any authenticated connection joins any recipient.</summary>
public sealed class AuthenticatedMembershipPolicy : IBackplaneMembershipPolicy
{
    /// <inheritdoc />
    public Task<bool> CanJoinAsync(ClaimsPrincipal user, Recipient recipient, CancellationToken cancellationToken = default)
        => Task.FromResult(Guard.NotNull(user).Identity?.IsAuthenticated == true);
}

/// <summary>
/// The hub the services connect to, mapped in the application's host by <c>MapBackplaneHub</c>, under the
/// authentication and the authorization the host declares. A connection joins the recipients it serves,
/// <see cref="Join"/>; it publishes with <see cref="Publish"/>, and the hub stamps the identity of the connection
/// on the event before relaying it, as <c>Receive(envelope)</c>, to the connections of its recipient — every
/// connection when the recipient is everyone.
/// </summary>
/// <remarks>
/// The hub acts on spaces: the recipient is the object of a join, named by the client and verified by the policy,
/// never a context. It belongs to <see cref="BackplaneModule"/> and performs <see cref="BackplaneOperations.Publish"/>.
/// </remarks>
[BelongsTo<BackplaneModule>]
[Operation(typeof(BackplaneOperations), nameof(BackplaneOperations.Publish))]
public sealed class BackplaneHub(EventRelay relay, IBackplaneMembershipPolicy policy) : Hub
{
    /// <summary>The method a connection receives events on, with the stamped <see cref="Envelope"/> as its argument.</summary>
    public const string ReceiveMethod = "Receive";

    /// <summary>
    /// Publishes <paramref name="envelope"/> to its recipient. The emitter and the moment are the hub's to stamp:
    /// whatever the envelope carries is replaced by the identity of the connection and the hub's clock.
    /// </summary>
    /// <exception cref="HubException">The envelope is malformed, or the connection carries no identity.</exception>
    public async Task Publish(Envelope envelope)
    {
        try
        {
            await relay.RelayAsync(envelope, Context.User, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (UserFacingException exception)
        {
            throw new HubException(exception.Message);
        }
    }

    /// <summary>
    /// Joins the recipients named by <paramref name="segments"/> — the compartments the service serves, its own
    /// name as <c>service:name</c> —, once the policy authorized each. Everyone, <c>_all</c>, is not joined: every
    /// connection receives what goes to everyone.
    /// </summary>
    /// <exception cref="HubException">A segment is not a recipient, names everyone, or the policy refuses.</exception>
    public async Task Join(string[] segments)
    {
        Guard.NotNull(segments);
        var user = Context.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        foreach (var recipient in segments.Select(ParseJoinable))
        {
            if (!await policy.CanJoinAsync(user, recipient, Context.ConnectionAborted).ConfigureAwait(false))
                throw new HubException($"Not authorized to receive the events of {recipient}.");
            await Groups.AddToGroupAsync(Context.ConnectionId, recipient.Segment, Context.ConnectionAborted).ConfigureAwait(false);
        }
    }

    /// <summary>Leaves the recipients named by <paramref name="segments"/>.</summary>
    public async Task Leave(string[] segments)
    {
        Guard.NotNull(segments);
        foreach (var recipient in segments.Select(ParseJoinable))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, recipient.Segment, Context.ConnectionAborted).ConfigureAwait(false);
    }

    private static Recipient ParseJoinable(string segment)
    {
        if (!Recipient.TryParse(segment, out var recipient))
            throw new HubException($"'{segment}' is not a recipient.");
        if (recipient.IsAll)
            throw new HubException("Everyone is not joined: every connection receives what goes to everyone.");
        return recipient;
    }
}
