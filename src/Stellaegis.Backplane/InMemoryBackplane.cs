using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane;

/// <summary>
/// The bus of a single host: publishes to the subscribers of the process, outside the call of the publisher.
/// <see cref="IEventPublisher"/> for the modules, <see cref="IEventBus"/> for the host. The emitter stamped on
/// each delivery is the identity the host gave the bus, or nothing.
/// </summary>
/// <remarks>
/// Publication returns once the event is queued; a background loop hands it to the subscribers in the order of
/// publication, each in its turn, and logs a subscriber that fails. Nothing reaches the publisher, as
/// <see cref="IEventPublisher"/> promises. An event published after the bus is disposed is dropped.
/// </remarks>
public sealed class InMemoryBackplane : IEventPublisher, IEventBus, IAsyncDisposable
{
    private readonly EventRegistry _registry;
    private readonly EventSubscriptions _subscriptions;
    private readonly ILogger<InMemoryBackplane> _logger;
    private readonly Actor? _emitter;
    private readonly Channel<(Envelope Envelope, TaskCompletionSource? Done)> _queue = Channel.CreateUnbounded<(Envelope, TaskCompletionSource?)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;

    /// <summary>Creates the bus of a process, stamping <paramref name="emitter"/> on each delivery when given.</summary>
    public InMemoryBackplane(EventRegistry registry, ILogger<InMemoryBackplane> logger, Actor? emitter = null)
    {
        _registry = Guard.NotNull(registry);
        _logger = Guard.NotNull(logger);
        _emitter = emitter;
        _subscriptions = new EventSubscriptions(registry, logger);
        _loop = Task.Run(DrainAsync);
    }

    /// <inheritdoc />
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IEvent
    {
        var envelope = _registry.Wrap(@event, _emitter, DateTimeOffset.UtcNow);
        if (!_queue.Writer.TryWrite((envelope, null)))
            _logger.LogWarning("The in-memory backplane is disposed: the event {Name} is dropped.", envelope.Name);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IEvent => _subscriptions.Subscribe(handler);

    /// <inheritdoc />
    public void SubscribeToDeliveries<TEvent>(Func<Delivery<TEvent>, CancellationToken, Task> handler) where TEvent : IEvent => _subscriptions.SubscribeToDeliveries(handler);

    /// <inheritdoc />
    public void Subscribe(string name, Func<RawDelivery, CancellationToken, Task> handler) => _subscriptions.Subscribe(name, handler);

    /// <summary>Completes once every event published before the call has reached its subscribers. For tests.</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite((new Envelope(string.Empty, default, Recipient.AllSegment, null, DateTimeOffset.UtcNow), done)))
            done.TrySetResult();
        return done.Task;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        await foreach (var (envelope, done) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (done is not null)
            {
                done.TrySetResult();
                continue;
            }
            await _subscriptions.DispatchAsync(envelope).ConfigureAwait(false);
        }
    }
}
