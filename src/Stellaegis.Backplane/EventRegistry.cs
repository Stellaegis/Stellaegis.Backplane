using System.Collections.Immutable;
using System.Text.Json;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane;

/// <summary>What the host declared about one event type: its name on the wire and how its recipient is found.</summary>
public sealed class EventRegistration
{
    internal EventRegistration(Type eventType, string name, Func<IEvent, Recipient> recipientOf)
    {
        EventType = eventType;
        Name = name;
        RecipientOf = recipientOf;
    }

    /// <summary>The .NET type of the event.</summary>
    public Type EventType { get; }

    /// <summary>The name on the wire, <c>invoice.issued</c>: identifiers joined by dots. The contract that stabilizes.</summary>
    public string Name { get; }

    /// <summary>The recipient of an event of this type, as the host declared it.</summary>
    public Func<IEvent, Recipient> RecipientOf { get; }
}

/// <summary>
/// The event types a host declared, read by every transport of the process: the name of a type, the type of a
/// name, the recipient of an event, and the JSON of a payload. Built once, by <see cref="BackplaneBuilder"/>, and
/// registered as a singleton; an instance per process, not a static.
/// </summary>
public sealed class EventRegistry
{
    private readonly ImmutableDictionary<string, EventRegistration> _byName;
    private readonly ImmutableDictionary<Type, EventRegistration> _byType;

    internal EventRegistry(IEnumerable<EventRegistration> registrations, JsonSerializerOptions jsonOptions)
    {
        var list = registrations.ToImmutableList();
        _byName = list.ToImmutableDictionary(registration => registration.Name, StringComparer.Ordinal);
        _byType = list.ToImmutableDictionary(registration => registration.EventType);
        Registrations = list;
        JsonOptions = jsonOptions;
    }

    /// <summary>Every registration, in the order of declaration.</summary>
    public IReadOnlyList<EventRegistration> Registrations { get; }

    /// <summary>The JSON options every payload is written and read with.</summary>
    public JsonSerializerOptions JsonOptions { get; }

    /// <summary>The registration of <paramref name="name"/>, or <see langword="null"/>.</summary>
    public EventRegistration? Find(string name) => _byName.GetValueOrDefault(Guard.NotNull(name));

    /// <summary>The registration of <paramref name="eventType"/>, or <see langword="null"/>.</summary>
    public EventRegistration? Find(Type eventType) => _byType.GetValueOrDefault(Guard.NotNull(eventType));

    /// <summary>The registration of <typeparamref name="TEvent"/>.</summary>
    /// <exception cref="InvalidOperationException">The type is not registered.</exception>
    public EventRegistration Require<TEvent>() where TEvent : IEvent
        => Find(typeof(TEvent)) ?? throw new InvalidOperationException(
            $"{typeof(TEvent).Name} is not registered on the backplane: declare it with "
            + $"AddStellaegisBackplane(b => b.Register<{typeof(TEvent).Name}>(\"name.on.the.wire\", e => Recipient...)).");

    /// <summary>
    /// Wraps <paramref name="event"/> for the wire: its name, its payload, its recipient as the registration names
    /// it, and the emitter and the moment stamped.
    /// </summary>
    /// <exception cref="InvalidOperationException">The type is not registered.</exception>
    public Envelope Wrap<TEvent>(TEvent @event, Actor? emitter, DateTimeOffset publishedAt) where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
        var registration = Require<TEvent>();
        var recipient = registration.RecipientOf(@event)
            ?? throw new InvalidOperationException($"The recipient of {registration.Name} resolved to null: a recipient is always named.");
        return new Envelope(registration.Name, JsonSerializer.SerializeToElement(@event, @event.GetType(), JsonOptions), recipient.Segment, emitter?.Value, publishedAt);
    }

    /// <summary>
    /// Reads the event of <paramref name="envelope"/> back, when its name is registered: the event and its
    /// registration, or <see langword="null"/> and <see langword="null"/> for a name this process does not know.
    /// </summary>
    /// <exception cref="JsonException">The payload does not read as the registered type.</exception>
    public (IEvent Event, EventRegistration Registration)? Unwrap(Envelope envelope)
    {
        Guard.NotNull(envelope);
        if (Find(envelope.Name) is not { } registration)
            return null;
        var @event = (IEvent?)envelope.Payload.Deserialize(registration.EventType, JsonOptions)
            ?? throw new JsonException($"The payload of {envelope.Name} read as null.");
        return (@event, registration);
    }
}

/// <summary>
/// Declares the events of a host to the backplane: each type, its name on the wire, and the recipient of each of
/// its events. A name is registered once, a type once, and nothing is relayed that was not declared.
/// <code>
/// services.AddStellaegisBackplane(b => b
///     .Register&lt;InvoiceIssued&gt;("invoice.issued", e => Recipient.Of(e.Compartment))
///     .Register&lt;UserBlocked&gt;("user.blocked", _ => Recipient.All));
/// </code>
/// </summary>
public sealed class BackplaneBuilder
{
    private readonly List<EventRegistration> _registrations = [];

    internal BackplaneBuilder()
    {
    }

    /// <summary>The JSON options every payload is written and read with. Web defaults unless changed.</summary>
    public JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Registers <typeparamref name="TEvent"/> under <paramref name="name"/>, identifiers joined by dots, with
    /// <paramref name="recipient"/> naming who each event goes to. An event of the shared space goes where the
    /// function says, and nowhere by default.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not identifiers joined by dots.</exception>
    /// <exception cref="InvalidOperationException">The name or the type is already registered.</exception>
    public BackplaneBuilder Register<TEvent>(string name, Func<TEvent, Recipient> recipient) where TEvent : IEvent
    {
        Guard.Satisfies(name, IsEventName, $"'{name}' is not an event name: identifiers joined by dots, such as invoice.issued, each {NameRules.IdentifierRule}.");
        Guard.NotNull(recipient);
        if (_registrations.Any(registration => string.Equals(registration.Name, name, StringComparison.Ordinal)))
            throw new InvalidOperationException($"The event name {name} is already registered: a name on the wire identifies one event type.");
        if (_registrations.Any(registration => registration.EventType == typeof(TEvent)))
            throw new InvalidOperationException($"{typeof(TEvent).Name} is already registered: an event type has one name on the wire.");
        _registrations.Add(new EventRegistration(typeof(TEvent), name, @event => recipient((TEvent)@event)));
        return this;
    }

    internal EventRegistry Build() => new(_registrations, JsonOptions);

    /// <summary>Whether <paramref name="value"/> is an event name: at least two identifiers joined by dots.</summary>
    public static bool IsEventName(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        var parts = value.Split('.');
        return parts.Length >= 2 && parts.All(NameRules.IsIdentifier);
    }
}
