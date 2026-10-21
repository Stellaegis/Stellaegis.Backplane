using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane;

/// <summary>
/// Where a host subscribes to the events of the backplane. An API of the host, never of a module: a module
/// publishes through <see cref="IEventPublisher"/> and subscribes to nothing. A subscription is taken at startup,
/// before the host runs.
/// <code>
/// bus.Subscribe&lt;UserBlocked&gt;((e, ct) => cache.EvictAsync(e.UserId, ct));
/// bus.SubscribeToDeliveries&lt;InvoiceIssued&gt;((delivery, ct) => audit.RecordAsync(delivery.Event, delivery.Emitter, ct));
/// </code>
/// </summary>
public interface IEventBus
{
    /// <summary>Subscribes to the events of <typeparamref name="TEvent"/>, which must be registered.</summary>
    void Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IEvent;

    /// <summary>
    /// Subscribes to the events of <typeparamref name="TEvent"/>, with the delivery around each: its recipient, its
    /// emitter, its moment. A name of its own, so that a lambda never hesitates between the two subscriptions.
    /// </summary>
    void SubscribeToDeliveries<TEvent>(Func<Delivery<TEvent>, CancellationToken, Task> handler) where TEvent : IEvent;

    /// <summary>
    /// Subscribes to the events named <paramref name="name"/>, as JSON: for a name this process does not have the
    /// type of — an event of another service, whose contract is its name.
    /// </summary>
    void Subscribe(string name, Func<RawDelivery, CancellationToken, Task> handler);
}

/// <summary>
/// The subscriptions of a process and their dispatch: what the in-process bus and the client of a hub share. A
/// handler that fails is logged and the next one runs; nothing reaches the publisher.
/// </summary>
public sealed class EventSubscriptions(EventRegistry registry, ILogger logger) : IEventBus
{
    private readonly EventRegistry _registry = Guard.NotNull(registry);
    private readonly ILogger _logger = Guard.NotNull(logger);
    private readonly ConcurrentDictionary<string, ImmutableList<Func<Envelope, IEvent?, CancellationToken, Task>>> _handlers = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IEvent
    {
        Guard.NotNull(handler);
        SubscribeToDeliveries<TEvent>((delivery, ct) => handler(delivery.Event, ct));
    }

    /// <inheritdoc />
    public void SubscribeToDeliveries<TEvent>(Func<Delivery<TEvent>, CancellationToken, Task> handler) where TEvent : IEvent
    {
        Guard.NotNull(handler);
        var registration = _registry.Require<TEvent>();
        Add(registration.Name, (envelope, @event, ct) => @event is TEvent typed
            ? handler(new Delivery<TEvent>(typed, Recipient.Parse(envelope.Recipient), ActorOf(envelope.Emitter), envelope.PublishedAt), ct)
            : Task.CompletedTask);
    }

    /// <inheritdoc />
    public void Subscribe(string name, Func<RawDelivery, CancellationToken, Task> handler)
    {
        Guard.Satisfies(name, BackplaneBuilder.IsEventName, $"'{name}' is not an event name.");
        Guard.NotNull(handler);
        Add(name, (envelope, _, ct) => handler(new RawDelivery(envelope.Name, envelope.Payload, Recipient.Parse(envelope.Recipient), ActorOf(envelope.Emitter), envelope.PublishedAt), ct));
    }

    /// <summary>Whether anything subscribed to <paramref name="name"/>.</summary>
    public bool HasSubscribers(string name) => _handlers.ContainsKey(Guard.NotNull(name));

    /// <summary>
    /// Hands <paramref name="envelope"/> to every subscriber of its name, in the order of subscription. The event is
    /// read once when the name is registered; a name this process does not know reaches its raw subscribers only.
    /// </summary>
    public async Task DispatchAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(envelope);
        if (!_handlers.TryGetValue(envelope.Name, out var handlers))
            return;

        IEvent? @event = null;
        try
        {
            @event = _registry.Unwrap(envelope)?.Event;
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "The payload of the event {Name} does not read as its registered type; its typed subscribers are skipped.", envelope.Name);
        }

        foreach (var handler in handlers)
        {
            try
            {
                await handler(envelope, @event, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "A subscriber of the event {Name} failed; the others run.", envelope.Name);
            }
        }
    }

    private void Add(string name, Func<Envelope, IEvent?, CancellationToken, Task> handler)
        => _handlers.AddOrUpdate(name, _ => [handler], (_, existing) => existing.Add(handler));

    private static Actor? ActorOf(string? emitter) => emitter is not null && Actor.TryParse(emitter, out var actor) ? actor : null;
}
