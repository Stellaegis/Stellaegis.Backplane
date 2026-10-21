using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Stellaegis.Backplane.PostgreSQL;
using Stellaegis.Backplane.Server;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.Tests;

/// <summary>The journal installed by the fixture, written and read under the application's role.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlJournalTests(PostgreSqlFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Envelope EnvelopeOf(string name, string recipient, string emitter, DateTimeOffset publishedAt, object payload)
        => new(name, JsonSerializer.SerializeToElement(payload), recipient, emitter, publishedAt);

    [Fact]
    public async Task The_journal_keeps_each_event_as_stamped_and_replays_what_a_recipient_may_see()
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? "");
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var journal = new PostgreSqlEventJournal(dataSource);
        var t0 = DateTimeOffset.UtcNow;

        await journal.AppendAsync(EnvelopeOf("widgets.changed", "acme", "svc-a", t0.AddSeconds(1), new { x = 1 }), Ct);
        await journal.AppendAsync(EnvelopeOf("widgets.changed", "globex", "svc-b", t0.AddSeconds(2), new { x = 2 }), Ct);
        await journal.AppendAsync(EnvelopeOf("users.blocked", "_all", "svc-a", t0.AddSeconds(3), new { user = "jane" }), Ct);
        await journal.AppendAsync(EnvelopeOf("vault.rotated", "service:identity", "svc-c", t0.AddSeconds(4), new { slot = 2 }), Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => journal.AppendAsync(EnvelopeOf("users.blocked", "_all", null!, t0, new { }), Ct));

        var acme = new List<StoredEvent>();
        await foreach (var stored in journal.ReadAsync(t0, [Recipient.Of(Events.Acme)], Ct))
            acme.Add(stored);
        Assert.Equal(["acme", "_all"], acme.Select(stored => stored.Envelope.Recipient));
        Assert.Equal("svc-a", acme[0].Envelope.Emitter);
        Assert.Equal(1, acme[0].Envelope.Payload.GetProperty("x").GetInt32());
        Assert.Equal(t0.AddSeconds(1).ToUniversalTime().ToUnixTimeMilliseconds(), acme[0].Envelope.PublishedAt.ToUnixTimeMilliseconds());
        Assert.True(acme[0].Id < acme[1].Id);

        var identity = new List<StoredEvent>();
        await foreach (var stored in journal.ReadAsync(t0.AddSeconds(3), [Recipient.Service("identity")], Ct))
            identity.Add(stored);
        Assert.Equal(["vault.rotated"], identity.Select(stored => stored.Envelope.Name));   // since excludes the third

        Assert.Equal(2, await journal.DeleteBeforeAsync(t0.AddSeconds(3), Ct));
        var left = new List<StoredEvent>();
        await foreach (var stored in journal.ReadAsync(t0, [Recipient.Of(Events.Acme), Recipient.Of(Events.Globex), Recipient.Service("identity")], Ct))
            left.Add(stored);
        Assert.Equal(["users.blocked", "vault.rotated"], left.Select(stored => stored.Envelope.Name));
        await journal.DeleteBeforeAsync(DateTimeOffset.MaxValue, Ct);
    }

    [Fact]
    public async Task The_hub_journals_on_PostgreSQL_and_the_host_verifies_the_database_at_startup()
    {
        Assert.SkipWhen(database.Unavailable is not null, database.Unavailable ?? "");
        await using var app = await TestApp.StartAsync(
            services => services
                .AddStellaegisFrame(ApplicationFrame.ManyCompartments())
                .AddStellaegisBackplane(b => Events.RegisterAll(b))
                .AddStellaegisBackplaneServer()
                .AddStellaegisBackplanePostgreSQL(o => o.ConnectionString = database.ConnectionString),
            app => app.MapBackplaneEndpoints().RequireAuthorization());
        await app.Services.VerifyStellaegisDatabasesAsync(Ct);
        Assert.Equal([BackplaneDatabase.Value], app.Services.GetStellaegisModuleDatabases());
        Assert.IsType<PostgreSqlEventJournal>(app.Services.GetRequiredService<IEventJournal>());

        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add(TestApp.ClaimHeaderPrefix + "client_id", "svc-http");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        using var published = await http.PostAsJsonAsync("/backplane/events", new Envelope("widgets.changed", JsonSerializer.SerializeToElement(new { x = 7 }), "acme", null, default), Ct);
        published.EnsureSuccessStatusCode();

        var replayed = await http.GetFromJsonAsync<List<Envelope>>($"/backplane/events?since={Uri.EscapeDataString(before.ToString("O"))}&recipients=acme", Ct);
        var envelope = Assert.Single(replayed!);
        Assert.Equal("svc-http", envelope.Emitter);
        Assert.Equal(7, envelope.Payload.GetProperty("x").GetInt32());
        await app.Services.GetRequiredService<IEventJournal>().DeleteBeforeAsync(DateTimeOffset.MaxValue, Ct);
    }
}
