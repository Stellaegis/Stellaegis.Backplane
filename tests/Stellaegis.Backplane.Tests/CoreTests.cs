using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.Tests;

public class RecipientTests
{
    [Fact]
    public void A_recipient_is_everyone_a_compartment_or_a_service()
    {
        Assert.Equal("_all", Recipient.All.Segment);
        Assert.True(Recipient.All.IsAll);
        Assert.Equal("acme", Recipient.Of(Events.Acme).Segment);
        Assert.Equal(Events.Acme, Recipient.Of(Events.Acme).Compartment);
        Assert.Equal("service:identity", Recipient.Service("identity").Segment);
        Assert.Equal("identity", Recipient.Service("identity").ServiceName);
        Assert.Equal(Recipient.Of(Events.Acme), Recipient.Parse("acme"));
        Assert.Equal(Recipient.All, Recipient.Parse("_all"));
        Assert.Equal(Recipient.Service("identity"), Recipient.Parse("service:identity"));
    }

    [Theory]
    [InlineData("Acme")]
    [InlineData("_shared")]
    [InlineData("service:")]
    [InlineData("service:iden-tity")]
    [InlineData("")]
    public void A_segment_outside_the_grammar_is_refused(string segment)
    {
        Assert.False(Recipient.TryParse(segment, out _));
        Assert.Throws<FormatException>(() => Recipient.Parse(segment));
        Assert.Throws<ArgumentException>(() => Recipient.Service("iden-tity"));
    }
}

public class RegistryTests
{
    private static EventRegistry Registry() => Events.RegisterAll(new BackplaneBuilder()).Build();

    [Fact]
    public void A_host_declares_each_event_with_its_name_and_its_recipient()
    {
        var registry = Registry();

        Assert.Equal(3, registry.Registrations.Count);
        Assert.Equal(typeof(WidgetChanged), registry.Find("widgets.changed")!.EventType);
        Assert.Equal("users.blocked", registry.Find(typeof(UserBlocked))!.Name);
        Assert.Null(registry.Find("nobody.knows"));

        var envelope = registry.Wrap(new WidgetChanged(Events.Acme, Guid.NewGuid(), DateTimeOffset.UtcNow), Actor.Parse("svc"), DateTimeOffset.UnixEpoch);
        Assert.Equal("widgets.changed", envelope.Name);
        Assert.Equal("acme", envelope.Recipient);
        Assert.Equal("svc", envelope.Emitter);
        Assert.Equal("acme", envelope.Payload.GetProperty("compartment").GetString());

        var unwrapped = registry.Unwrap(envelope);
        Assert.Equal(Events.Acme, Assert.IsType<WidgetChanged>(unwrapped!.Value.Event).Compartment);
        Assert.Null(registry.Unwrap(envelope with { Name = "nobody.knows" }));
    }

    [Fact]
    public void A_name_and_a_type_are_registered_once()
    {
        var twiceTheName = Assert.Throws<InvalidOperationException>(() => new BackplaneBuilder()
            .Register<WidgetChanged>("widgets.changed", e => Recipient.Of(e.Compartment))
            .Register<UserBlocked>("widgets.changed", _ => Recipient.All));
        Assert.Contains("widgets.changed is already registered", twiceTheName.Message);

        var twiceTheType = Assert.Throws<InvalidOperationException>(() => new BackplaneBuilder()
            .Register<WidgetChanged>("widgets.changed", e => Recipient.Of(e.Compartment))
            .Register<WidgetChanged>("widgets.moved", e => Recipient.Of(e.Compartment)));
        Assert.Contains("WidgetChanged is already registered", twiceTheType.Message);

        Assert.Throws<ArgumentException>(() => new BackplaneBuilder().Register<UserBlocked>("blocked", _ => Recipient.All));
        Assert.Throws<ArgumentException>(() => new BackplaneBuilder().Register<UserBlocked>("users.Blocked", _ => Recipient.All));
    }

    [Fact]
    public void An_unregistered_event_cannot_be_wrapped_nor_subscribed()
    {
        var registry = Registry();

        var exception = Assert.Throws<InvalidOperationException>(() => registry.Wrap(new Unregistered(DateTimeOffset.UtcNow), null, DateTimeOffset.UtcNow));
        Assert.Contains("Unregistered is not registered on the backplane", exception.Message);
        Assert.Throws<InvalidOperationException>(() => new EventSubscriptions(registry, NullLogger.Instance).Subscribe<Unregistered>((_, _) => Task.CompletedTask));
    }

    [Fact]
    public void The_backplane_is_registered_once_and_declares_its_module()
    {
        var services = new ServiceCollection()
            .AddStellaegisFrame(ApplicationFrame.ManyCompartments())
            .AddStellaegisBackplane(b => Events.RegisterAll(b));
        Assert.Throws<InvalidOperationException>(() => services.AddStellaegisBackplane(_ => { }));

        using var provider = services.BuildServiceProvider();
        provider.ValidateStellaegisModules();
        var module = provider.GetStellaegisModule<BackplaneModule>()!;
        Assert.Equal(ModuleCompartments.None, module.Compartments);
        Assert.Equal(["events.publish", "events.replay"], module.Operations.Select(operation => operation.Identity));
        new ServiceCollection().AddStellaegisBackplane(_ => { }).BuildServiceProvider().ValidateStellaegisModules();   // fits a frame without compartments
    }
}

public class InMemoryBackplaneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(Actor? emitter = null) => new ServiceCollection()
        .AddLogging()
        .AddStellaegisFrame(ApplicationFrame.ManyCompartments())
        .AddStellaegisBackplane(b => Events.RegisterAll(b))
        .AddStellaegisInMemoryBackplane(emitter)
        .BuildServiceProvider();

    [Fact]
    public async Task A_published_event_reaches_its_subscribers_outside_the_publisher_call()
    {
        await using var provider = Build(Actor.Parse("svc:api"));
        var bus = provider.GetRequiredService<IEventBus>();
        var publisher = provider.GetRequiredService<IEventPublisher>();
        Assert.Same(bus, publisher);

        var deliveries = new List<Delivery<WidgetChanged>>();
        var handlerRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.SubscribeToDeliveries<WidgetChanged>((delivery, _) => { deliveries.Add(delivery); handlerRan.TrySetResult(); return Task.CompletedTask; });
        var raw = new List<RawDelivery>();
        bus.Subscribe("widgets.changed", (delivery, _) => { raw.Add(delivery); return Task.CompletedTask; });

        var publish = publisher.PublishAsync(new WidgetChanged(Events.Acme, Guid.NewGuid(), DateTimeOffset.UtcNow), Ct);
        Assert.True(publish.IsCompleted);   // queued, not dispatched in the call
        await handlerRan.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await provider.GetRequiredService<InMemoryBackplane>().FlushAsync();

        var delivery = Assert.Single(deliveries);
        Assert.Equal(Recipient.Of(Events.Acme), delivery.Recipient);
        Assert.Equal(Actor.Parse("svc:api"), delivery.Emitter);
        Assert.Equal("widgets.changed", Assert.Single(raw).Name);
    }

    [Fact]
    public async Task A_failing_subscriber_is_logged_and_the_next_one_runs()
    {
        await using var provider = Build();
        var bus = provider.GetRequiredService<IEventBus>();
        var second = new TaskCompletionSource<UserBlocked>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.Subscribe<UserBlocked>((_, _) => throw new InvalidOperationException("boom"));
        bus.Subscribe<UserBlocked>((e, _) => { second.TrySetResult(e); return Task.CompletedTask; });

        await provider.GetRequiredService<IEventPublisher>().PublishAsync(new UserBlocked("jane", DateTimeOffset.UtcNow), Ct);

        Assert.Equal("jane", (await second.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).UserId);
    }

    [Fact]
    public async Task Events_are_delivered_in_the_order_of_publication_and_the_emitter_may_be_absent()
    {
        await using var provider = Build();
        var bus = provider.GetRequiredService<InMemoryBackplane>();
        var seen = new List<string>();
        Actor? emitter = Actor.Parse("x");
        bus.SubscribeToDeliveries<UserBlocked>((d, _) => { seen.Add(d.Event.UserId); emitter = d.Emitter; return Task.CompletedTask; });

        foreach (var user in new[] { "a", "b", "c" })
            await bus.PublishAsync(new UserBlocked(user, DateTimeOffset.UtcNow), Ct);
        await bus.FlushAsync();

        Assert.Equal(["a", "b", "c"], seen);
        Assert.Null(emitter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bus.PublishAsync(new Unregistered(DateTimeOffset.UtcNow), Ct));
    }

    [Fact]
    public async Task The_journal_in_memory_replays_what_a_recipient_may_see()
    {
        var journal = new InMemoryEventJournal();
        var t0 = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToElement(new { x = 1 });
        await journal.AppendAsync(new Envelope("widgets.changed", payload, "acme", "svc", t0.AddSeconds(1)), Ct);
        await journal.AppendAsync(new Envelope("widgets.changed", payload, "globex", "svc", t0.AddSeconds(2)), Ct);
        await journal.AppendAsync(new Envelope("users.blocked", payload, "_all", "svc", t0.AddSeconds(3)), Ct);

        var acme = new List<StoredEvent>();
        await foreach (var stored in journal.ReadAsync(t0, [Recipient.Of(Events.Acme)], Ct))
            acme.Add(stored);

        Assert.Equal(["acme", "_all"], acme.Select(stored => stored.Envelope.Recipient));
        Assert.Equal(2, await journal.DeleteBeforeAsync(t0.AddSeconds(3), Ct));
    }
}
