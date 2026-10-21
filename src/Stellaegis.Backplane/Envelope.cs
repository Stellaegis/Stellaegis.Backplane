using System.Text.Json;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane;

/// <summary>
/// An event as it travels: its name on the wire, its payload, its recipient's segment, the emitter the transport
/// stamped, and when it was published. The one shape of the hub's protocol, of the HTTP endpoints and of the
/// journal, so that a client and a hub of different versions still read each other.
/// </summary>
/// <remarks>
/// The emitter is never declared by the publisher of a transported event: the hub stamps it from the identity of
/// the connection that published, so that a service cannot speak in the name of another. In process, the bus
/// stamps the identity its host gave it, or nothing.
/// </remarks>
public sealed record Envelope(string Name, JsonElement Payload, string Recipient, string? Emitter, DateTimeOffset PublishedAt)
{
    /// <summary>The envelope with <paramref name="emitter"/> and <paramref name="publishedAt"/> stamped on it.</summary>
    public Envelope Stamped(Actor? emitter, DateTimeOffset publishedAt) => this with { Emitter = emitter?.Value, PublishedAt = publishedAt };
}

/// <summary>What a subscriber receives of a typed event: the event, and the delivery around it.</summary>
public sealed record Delivery<TEvent>(TEvent Event, Recipient Recipient, Actor? Emitter, DateTimeOffset PublishedAt)
    where TEvent : IEvent;

/// <summary>What a subscriber by name receives: the payload as JSON, and the delivery around it.</summary>
public sealed record RawDelivery(string Name, JsonElement Payload, Recipient Recipient, Actor? Emitter, DateTimeOffset PublishedAt);
