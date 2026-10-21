using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stellaegis.Backplane.Client;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.Tests;

/// <summary>Two hosts on the client, each serving its compartments, exchanging events through the hub.</summary>
public class ClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Routes the connection of a client host to the test server, authenticated as the service named.</summary>
    private sealed class ToTestServer(WebApplication app, string clientId) : IHubConnectionCustomizer, IBackplaneTokenProvider
    {
        public void Configure(HttpConnectionOptions options)
        {
            options.HttpMessageHandlerFactory = _ => app.GetTestServer().CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.Headers.Add(TestApp.ClaimHeaderPrefix + "client_id", clientId);
        }

        public ValueTask<string?> GetTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
    }

    /// <summary>Authenticates the HTTP fallback of a client host on the test server, where the token is a header claim.</summary>
    private sealed class HeaderClaims(string clientId) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(TestApp.ClaimHeaderPrefix + "client_id", clientId);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// A client host, configured as a real one is: from its configuration section, or in code when
    /// <paramref name="inCode"/> is set.
    /// </summary>
    private static async Task<IHost> StartClientHostAsync(WebApplication server, string clientId, string hubPath = "/backplane/hub", bool inCode = false)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (!inCode)
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stellaegis:Backplane:Client:BaseUrl"] = "http://localhost",
                ["Stellaegis:Backplane:Client:HubPath"] = hubPath,
                ["Stellaegis:Backplane:Client:Compartments:0"] = clientId,
                ["Stellaegis:Backplane:Client:StartupAttempts"] = "2",
                ["Stellaegis:Backplane:Client:RetryDelay"] = "00:00:00.200",
            });
        builder.Services
            .AddStellaegisFrame(ApplicationFrame.ManyCompartments())
            .AddStellaegisBackplane(b => Events.RegisterAll(b))
            .AddSingleton<IHubConnectionCustomizer>(new ToTestServer(server, clientId));
        if (inCode)
            builder.Services.AddStellaegisBackplaneClient(o =>
            {
                o.BaseUrl = "http://localhost";
                o.HubPath = hubPath;
                o.Compartments = [clientId];
                o.StartupAttempts = 2;
                o.RetryDelay = TimeSpan.FromMilliseconds(200);
            });
        else
            builder.Services.AddStellaegisBackplaneClient(builder.Configuration);
        builder.Services
            .ConfigureHttpClientDefaults(http => http
                .ConfigurePrimaryHttpMessageHandler(() => server.GetTestServer().CreateHandler())
                .AddHttpMessageHandler(() => new HeaderClaims(clientId)));
        var host = builder.Build();
        await host.StartAsync(Ct);
        return host;
    }

    [Fact]
    public async Task Each_host_receives_the_events_of_its_compartments_and_of_everyone_stamped_with_their_emitter()
    {
        await using var server = await HubTests.StartHostAsync();
        using var acme = await StartClientHostAsync(server, "acme");
        using var globex = await StartClientHostAsync(server, "globex", inCode: true);
        var atAcme = Channel.CreateUnbounded<Delivery<WidgetChanged>>();
        var atGlobex = Channel.CreateUnbounded<Delivery<WidgetChanged>>();
        var blockedAtGlobex = new TaskCompletionSource<UserBlocked>(TaskCreationOptions.RunContinuationsAsynchronously);
        acme.Services.GetRequiredService<IEventBus>().SubscribeToDeliveries<WidgetChanged>((d, _) => { atAcme.Writer.TryWrite(d); return Task.CompletedTask; });
        globex.Services.GetRequiredService<IEventBus>().SubscribeToDeliveries<WidgetChanged>((d, _) => { atGlobex.Writer.TryWrite(d); return Task.CompletedTask; });
        globex.Services.GetRequiredService<IEventBus>().Subscribe<UserBlocked>((e, _) => { blockedAtGlobex.TrySetResult(e); return Task.CompletedTask; });

        var publisher = acme.Services.GetRequiredService<IEventPublisher>();
        Assert.IsType<SignalRBackplane>(publisher);
        var forGlobex = new WidgetChanged(Events.Globex, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await publisher.PublishAsync(forGlobex, Ct);
        await publisher.PublishAsync(new UserBlocked("jane", DateTimeOffset.UtcNow), Ct);
        var forAcme = new WidgetChanged(Events.Acme, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await globex.Services.GetRequiredService<IEventPublisher>().PublishAsync(forAcme, Ct);

        var deliveredToGlobex = await atGlobex.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
        Assert.Equal(forGlobex, deliveredToGlobex.Event);
        Assert.Equal(Actor.Parse("acme"), deliveredToGlobex.Emitter);
        Assert.Equal(Recipient.Of(Events.Globex), deliveredToGlobex.Recipient);
        Assert.Equal("jane", (await blockedAtGlobex.Task.WaitAsync(Timeout, Ct)).UserId);
        var deliveredToAcme = await atAcme.Reader.ReadAsync(Ct).AsTask().WaitAsync(Timeout, Ct);
        Assert.Equal(forAcme, deliveredToAcme.Event);
        Assert.Equal(Actor.Parse("globex"), deliveredToAcme.Emitter);

        await Task.Delay(200, Ct);
        Assert.Equal(0, atAcme.Reader.Count);   // acme never sees what went to globex
        await acme.StopAsync(Ct);
        await globex.StopAsync(Ct);
    }

    [Fact]
    public async Task A_host_whose_hub_is_unreachable_refuses_to_start_and_its_options_are_validated()
    {
        await using var server = await HubTests.StartHostAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => StartClientHostAsync(server, "acme", hubPath: "/nowhere"));

        Assert.Equal("Compartments: 'Acme' is not a compartment.", new BackplaneClientOptions { BaseUrl = "http://h", Compartments = ["Acme"] }.FindError());
        Assert.Contains("ServiceName must be an identifier", new BackplaneClientOptions { BaseUrl = "http://h", ServiceName = "Id" }.FindError());
        Assert.Equal("BaseUrl is required, as an absolute http or https URL.", new BackplaneClientOptions().FindError());
        Assert.Null(new BackplaneClientOptions { BaseUrl = "http://h", ServiceName = "identity", Compartments = ["acme"] }.FindError());
        Assert.Equal([Recipient.Of(Events.Acme), Recipient.Service("identity")], new BackplaneClientOptions { BaseUrl = "http://h", ServiceName = "identity", Compartments = ["acme"] }.Recipients());
    }
}
