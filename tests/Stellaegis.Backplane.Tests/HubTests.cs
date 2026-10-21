using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stellaegis.Backplane.Server;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.Tests;

/// <summary>The hub in the application's host, under its authentication, routing each event to the connections of its recipient.</summary>
public class HubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The application's rule in most tests: every authenticated service joins any recipient.</summary>
    internal sealed class TrustedServices : IBackplaneMembershipPolicy
    {
        public Task<bool> CanJoinAsync(System.Security.Claims.ClaimsPrincipal user, Recipient recipient, CancellationToken cancellationToken = default)
            => Task.FromResult(user.Identity?.IsAuthenticated == true);
    }

    internal static Task<WebApplication> StartHostAsync(Action<WebApplication>? map = null, bool journal = true) => TestApp.StartAsync(
        services =>
        {
            services
                .AddStellaegisFrame(ApplicationFrame.ManyCompartments())
                .AddStellaegisBackplane(b => Events.RegisterAll(b))
                .AddStellaegisBackplaneServer<TrustedServices>();
            if (journal)
                services.AddStellaegisInMemoryEventJournal();
        },
        map ?? (app =>
        {
            app.MapBackplaneHub().RequireAuthorization();
            app.MapBackplaneEndpoints().RequireAuthorization();
        }));

    internal static HubConnection Connect(WebApplication app, params (string Type, string Value)[] claims)
        => new HubConnectionBuilder()
            .WithUrl("http://localhost/backplane/hub", options =>
            {
                options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                foreach (var (type, value) in claims)
                    options.Headers.Add(TestApp.ClaimHeaderPrefix + type, value);
            })
            .Build();

    private static async Task<(HubConnection Connection, Channel<Envelope> Received)> JoinAsync(WebApplication app, string clientId, params string[] segments)
    {
        var connection = Connect(app, ("client_id", clientId));
        var received = Channel.CreateUnbounded<Envelope>();
        connection.On<Envelope>(BackplaneHub.ReceiveMethod, envelope => received.Writer.TryWrite(envelope));
        await connection.StartAsync(Ct);
        await connection.InvokeAsync("Join", segments, Ct);
        return (connection, received);
    }

    private static Envelope EnvelopeOf(string name, string recipient, object payload)
        => new(name, JsonSerializer.SerializeToElement(payload), recipient, "forged", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task An_event_reaches_the_connections_of_its_recipient_and_only_them_stamped_by_the_hub()
    {
        await using var app = await StartHostAsync();
        var (acme, acmeReceived) = await JoinAsync(app, "svc-acme", "acme");
        var (globex, globexReceived) = await JoinAsync(app, "svc-globex", "globex", "service:identity");
        await using var _ = acme;
        await using var __ = globex;

        await acme.InvokeAsync("Publish", EnvelopeOf("widgets.changed", "globex", new { x = 1 }), Ct);
        await globex.InvokeAsync("Publish", EnvelopeOf("widgets.changed", "acme", new { x = 2 }), Ct);
        await acme.InvokeAsync("Publish", EnvelopeOf("vault.rotated", "service:identity", new { slot = 3 }), Ct);
        await globex.InvokeAsync("Publish", EnvelopeOf("users.blocked", "_all", new { user = "jane" }), Ct);

        var atAcme = await acmeReceived.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
        Assert.Equal(2, atAcme.Payload.GetProperty("x").GetInt32());
        Assert.Equal("svc-globex", atAcme.Emitter);                                   // stamped by the hub, not "forged"
        Assert.True(atAcme.PublishedAt > DateTimeOffset.UnixEpoch);
        var everyoneAtAcme = await acmeReceived.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
        Assert.Equal("users.blocked", everyoneAtAcme.Name);

        var atGlobex = new List<Envelope>();
        for (var i = 0; i < 3; i++)
            atGlobex.Add(await globexReceived.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct));
        Assert.Equal(["widgets.changed", "vault.rotated", "users.blocked"], atGlobex.Select(envelope => envelope.Name));
        Assert.Equal(["svc-acme", "svc-acme", "svc-globex"], atGlobex.Select(envelope => envelope.Emitter));

        await Task.Delay(200, Ct);
        Assert.Equal(0, acmeReceived.Reader.Count);                                   // nothing of globex, nothing of identity
    }

    [Fact]
    public async Task A_malformed_envelope_and_a_join_outside_the_grammar_are_refused()
    {
        await using var app = await StartHostAsync();
        await using var connection = Connect(app, ("sub", "jane"));
        await connection.StartAsync(Ct);

        var name = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Publish", EnvelopeOf("blocked", "_all", new { }), Ct));
        Assert.Contains("is not an event name", name.Message);
        var recipient = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Publish", EnvelopeOf("users.blocked", "Acme", new { }), Ct));
        Assert.Contains("is not a recipient", recipient.Message);
        var all = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Join", new[] { "_all" }, Ct));
        Assert.Contains("Everyone is not joined", all.Message);
    }

    [Fact]
    public async Task A_policy_of_the_application_decides_who_joins_what()
    {
        await using var app = await TestApp.StartAsync(
            services => services
                .AddStellaegisFrame(ApplicationFrame.ManyCompartments())
                .AddStellaegisBackplane(b => Events.RegisterAll(b))
                .AddStellaegisBackplaneServer<OnlyAcme>(),
            app => app.MapBackplaneHub().RequireAuthorization());
        await using var connection = Connect(app, ("sub", "jane"));
        await connection.StartAsync(Ct);

        await connection.InvokeAsync("Join", new[] { "acme" }, Ct);
        var refused = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Join", new[] { "globex" }, Ct));
        Assert.Contains("Not authorized to receive the events of globex", refused.Message);
    }

    private sealed class OnlyAcme : IBackplaneMembershipPolicy
    {
        public Task<bool> CanJoinAsync(System.Security.Claims.ClaimsPrincipal user, Recipient recipient, CancellationToken cancellationToken = default)
            => Task.FromResult(recipient.Compartment == Events.Acme);
    }

    [Fact]
    public async Task The_http_routes_publish_as_the_hub_does_and_replay_from_the_journal()
    {
        await using var app = await StartHostAsync();
        var (acme, acmeReceived) = await JoinAsync(app, "svc-acme", "acme");
        await using var _ = acme;
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add(TestApp.ClaimHeaderPrefix + "client_id", "svc-http");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        using var published = await http.PostAsJsonAsync("/backplane/events", EnvelopeOf("widgets.changed", "acme", new { x = 9 }), Ct);
        Assert.Equal(HttpStatusCode.Accepted, published.StatusCode);
        var stamped = (await published.Content.ReadFromJsonAsync<Envelope>(Ct))!;
        Assert.Equal("svc-http", stamped.Emitter);
        var received = await acmeReceived.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
        Assert.Equal((stamped.Name, stamped.Recipient, stamped.Emitter, stamped.PublishedAt), (received.Name, received.Recipient, received.Emitter, received.PublishedAt));
        Assert.Equal(9, received.Payload.GetProperty("x").GetInt32());

        using var other = await http.PostAsJsonAsync("/backplane/events", EnvelopeOf("widgets.changed", "globex", new { x = 10 }), Ct);
        using var everyone = await http.PostAsJsonAsync("/backplane/events", EnvelopeOf("users.blocked", "_all", new { }), Ct);

        var replayed = await http.GetFromJsonAsync<List<Envelope>>($"/backplane/events?since={Uri.EscapeDataString(before.ToString("O"))}&recipients=acme", Ct);
        Assert.Equal(["widgets.changed", "users.blocked"], replayed!.Select(envelope => envelope.Name));
        Assert.Equal(["acme", "_all"], replayed!.Select(envelope => envelope.Recipient));

        using var malformed = await http.PostAsJsonAsync("/backplane/events", EnvelopeOf("users.blocked", "Acme", new { }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var badRecipient = await http.GetAsync("/backplane/events?since=2026-01-01T00:00:00Z&recipients=_shared", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badRecipient.StatusCode);
    }

    [Fact]
    public async Task Without_a_journal_the_replay_says_so_and_a_caller_without_identity_is_refused()
    {
        await using var app = await StartHostAsync(journal: false);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add(TestApp.ClaimHeaderPrefix + "role", "admin");   // authenticated, without an identity claim

        using var replay = await http.GetAsync("/backplane/events?since=2026-01-01T00:00:00Z", Ct);
        Assert.Equal(HttpStatusCode.PreconditionFailed, replay.StatusCode);
        using var published = await http.PostAsJsonAsync("/backplane/events", EnvelopeOf("users.blocked", "_all", new { }), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, published.StatusCode);
        Assert.Contains("Emitter.Unknown", await published.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_anonymous_connection_is_refused_by_the_host()
    {
        await using var app = await StartHostAsync();
        await using var connection = Connect(app);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(Ct));
    }

    [Fact]
    public async Task A_hub_or_routes_mapped_without_authorization_fail_the_startup()
    {
        var hub = await Assert.ThrowsAsync<InvalidOperationException>(() => StartHostAsync(app => app.MapBackplaneHub()));
        Assert.Contains("events.publish", hub.Message);
        Assert.Contains("declare no authorization", hub.Message);

        var routes = await Assert.ThrowsAsync<InvalidOperationException>(() => StartHostAsync(app => app.MapBackplaneEndpoints()));
        Assert.Contains("declare no authorization", routes.Message);
    }

    [Fact]
    public async Task The_options_of_the_hub_are_validated_at_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Stellaegis:Backplane:Server:EventRetention"] = "00:00:00" })
            .Build();
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => TestApp.StartAsync(
            services => services
                .AddStellaegisFrame(ApplicationFrame.ManyCompartments())
                .AddStellaegisBackplane(b => Events.RegisterAll(b))
                .AddStellaegisBackplaneServer<TrustedServices>(configuration),
            app => app.MapBackplaneHub().RequireAuthorization()));
        Assert.Contains("EventRetention must be positive", exception.Message);

        Assert.Equal("EventRetention must be positive.", new BackplaneServerOptions { EventRetention = TimeSpan.Zero }.FindError());
        Assert.Null(new BackplaneServerOptions().FindError());
    }
}
