using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane.Client;

/// <summary>Adjusts the connection to the hub before it starts: for the tests, which route it to a test server.</summary>
internal interface IHubConnectionCustomizer
{
    void Configure(HttpConnectionOptions options);
}

/// <summary>
/// The bus of a host reaching the hub: <see cref="IEventPublisher"/> for the modules, <see cref="IEventBus"/> for
/// the host. An event published goes to the hub over the connection, or over HTTP while the connection is down,
/// from a queue that drops its oldest event when full; what the hub relays to this host reaches its subscribers,
/// outside the connection's handler, in the order of arrival. After a reconnection the host joins its recipients
/// again and replays, from the hub's journal, what was published meanwhile.
/// </summary>
/// <remarks>
/// The emitter of an event is stamped by the hub, from the identity this host presents: the client sends none.
/// A host receives its own events as any other, when they go to a recipient it serves or to everyone — nothing is
/// delivered locally without passing through the hub, so that every host sees one order.
/// </remarks>
public sealed class SignalRBackplane : IEventPublisher, IEventBus, IAsyncDisposable
{
    private readonly EventRegistry _registry;
    private readonly BackplaneClientOptions _options;
    private readonly IBackplaneEndpoints _endpoints;
    private readonly ILogger<SignalRBackplane> _logger;
    private readonly EventSubscriptions _subscriptions;
    private readonly IReadOnlyList<Recipient> _recipients;
    private readonly string[] _segments;
    private readonly HubConnection _connection;
    private readonly Channel<Envelope> _outgoing;
    private readonly Channel<Envelope> _incoming = Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _sender;
    private readonly Task _receiver;
    private DateTimeOffset? _lastReceived;
    private int _disposed;

    internal SignalRBackplane(
        EventRegistry registry,
        IOptions<BackplaneClientOptions> options,
        IBackplaneEndpoints endpoints,
        IServiceProvider services,
        ILogger<SignalRBackplane> logger)
    {
        _registry = Guard.NotNull(registry);
        _options = Guard.NotNull(options).Value;
        _endpoints = Guard.NotNull(endpoints);
        _logger = Guard.NotNull(logger);
        _subscriptions = new EventSubscriptions(registry, logger);
        _recipients = _options.Recipients();
        _segments = _recipients.Select(recipient => recipient.Segment).ToArray();
        _outgoing = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(_options.QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        var tokens = services.GetService(typeof(IBackplaneTokenProvider)) as IBackplaneTokenProvider;
        var customizers = (services.GetService(typeof(IEnumerable<IHubConnectionCustomizer>)) as IEnumerable<IHubConnectionCustomizer>) ?? [];
        _connection = new HubConnectionBuilder()
            .WithUrl(_options.BaseUrl.TrimEnd('/') + _options.HubPath, http =>
            {
                if (tokens is not null)
                    http.AccessTokenProvider = async () => await tokens.GetTokenAsync(_stopping.Token).ConfigureAwait(false);
                foreach (var customizer in customizers)
                    customizer.Configure(http);
            })
            .WithAutomaticReconnect(new SteadyRetryPolicy(_options.RetryDelay))
            .Build();
        _connection.On<Envelope>(ReceiveMethod, envelope => _incoming.Writer.TryWrite(envelope));
        _connection.Reconnected += _ => RejoinAsync();
        _connection.Closed += exception =>
        {
            if (exception is not null)
                _logger.LogWarning(exception, "The connection to the backplane hub closed.");
            return Task.CompletedTask;
        };

        _sender = Task.Run(SendAsync);
        _receiver = Task.Run(ReceiveAsync);
    }

    /// <summary>The method the hub sends events on.</summary>
    internal const string ReceiveMethod = "Receive";

    /// <summary>The recipients this host serves, from its options.</summary>
    public IReadOnlyList<Recipient> Recipients => _recipients;

    /// <summary>Whether the connection to the hub is up.</summary>
    public bool IsConnected => _connection.State == HubConnectionState.Connected;

    /// <summary>Connects to the hub and joins the recipients of this host. Throws when the hub is not reached.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
        await JoinAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : IEvent
    {
        var envelope = _registry.Wrap(@event, emitter: null, DateTimeOffset.UtcNow);
        if (!_outgoing.Writer.TryWrite(envelope))
            _logger.LogWarning("The backplane client is disposed: the event {Name} is dropped.", envelope.Name);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IEvent => _subscriptions.Subscribe(handler);

    /// <inheritdoc />
    public void SubscribeToDeliveries<TEvent>(Func<Delivery<TEvent>, CancellationToken, Task> handler) where TEvent : IEvent => _subscriptions.SubscribeToDeliveries(handler);

    /// <inheritdoc />
    public void Subscribe(string name, Func<RawDelivery, CancellationToken, Task> handler) => _subscriptions.Subscribe(name, handler);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;   // registered under several services, disposed once
        await _stopping.CancelAsync().ConfigureAwait(false);
        _outgoing.Writer.TryComplete();
        _incoming.Writer.TryComplete();
        await Task.WhenAll(_sender, _receiver).ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private Task JoinAsync(CancellationToken cancellationToken)
        => _segments.Length == 0 ? Task.CompletedTask : _connection.InvokeAsync("Join", _segments, cancellationToken);

    private async Task RejoinAsync()
    {
        try
        {
            await JoinAsync(_stopping.Token).ConfigureAwait(false);
            if (_options.ReplayOnReconnect && _lastReceived is { } since)
            {
                foreach (var envelope in await _endpoints.ReplayAsync(since, _recipients, _stopping.Token).ConfigureAwait(false))
                    _incoming.Writer.TryWrite(envelope);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "The host could not rejoin its recipients or replay after reconnecting to the backplane hub.");
        }
    }

    private async Task SendAsync()
    {
        var reader = _outgoing.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_stopping.Token).ConfigureAwait(false) && reader.TryPeek(out var envelope))
            {
                if (await TrySendAsync(envelope).ConfigureAwait(false))
                    reader.TryRead(out _);
                else
                    await Task.Delay(_options.RetryDelay, _stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Disposed: what is still queued is dropped, and logged below.
        }
        if (reader.Count > 0)
            _logger.LogWarning("The backplane client is disposed with {Count} events not yet published.", reader.Count);
    }

    private async Task<bool> TrySendAsync(Envelope envelope)
    {
        if (IsConnected)
        {
            try
            {
                await _connection.InvokeAsync("Publish", envelope, _stopping.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "The event {Name} could not be published over the connection; trying HTTP.", envelope.Name);
            }
        }
        try
        {
            await _endpoints.PublishAsync(envelope, _stopping.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The event {Name} could not be published over HTTP; retrying in {Delay}.", envelope.Name, _options.RetryDelay);
            return false;
        }
    }

    private async Task ReceiveAsync()
    {
        await foreach (var envelope in _incoming.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_lastReceived is null || envelope.PublishedAt > _lastReceived)
                _lastReceived = envelope.PublishedAt;
            await _subscriptions.DispatchAsync(envelope, _stopping.Token).ConfigureAwait(false);
        }
    }

    private sealed class SteadyRetryPolicy(TimeSpan delay) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext) => delay;
    }
}
