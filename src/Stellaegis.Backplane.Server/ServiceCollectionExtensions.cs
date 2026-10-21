using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stellaegis.Primitives;
using Stellaegis.Primitives.AspNetCore;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.Server;

/// <summary>
/// Registers and maps the hub of the backplane in the application's host.
/// </summary>
/// <remarks>
/// The hub lives under the authentication the host declares: this package registers no scheme of its own, so
/// that a single-process application and a dedicated host map it the same way. SignalR reads a bearer token from
/// the <c>Authorization</c> header; a WebSocket connection cannot send one, and a JWT host lets its scheme read
/// <c>access_token</c> from the query string on the hub's path — the README shows the lines.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>The configuration section of the hub's options, <c>Stellaegis:Backplane:Server</c>.</summary>
    public const string SectionName = "Stellaegis:Backplane:Server";

    /// <summary>
    /// Registers the hub, SignalR, the startup check of its routes, the relay, the membership policy — the
    /// application's <see cref="IBackplaneMembershipPolicy"/> if it registered one before, else
    /// <see cref="AuthenticatedMembershipPolicy"/> —, the options bound from <paramref name="sectionName"/>, and
    /// the cleanup of the journal. Call it beside <c>AddStellaegisBackplane</c>, which declares the events and
    /// the module, and beside the journal of the host, if any.
    /// </summary>
    public static IServiceCollection AddStellaegisBackplaneServer(this IServiceCollection services, IConfiguration configuration, string sectionName = SectionName)
    {
        Guard.NotNull(services);
        Guard.NotNull(configuration);
        services.AddStellaegisOptions<BackplaneServerOptions>(configuration, sectionName);
        return Server(services);
    }

    /// <summary>
    /// Registers the hub as <see cref="AddStellaegisBackplaneServer(IServiceCollection, IConfiguration, string)"/>
    /// does, with its options configured in code — the defaults unless <paramref name="configure"/> changes them.
    /// </summary>
    public static IServiceCollection AddStellaegisBackplaneServer(this IServiceCollection services, Action<BackplaneServerOptions>? configure = null)
    {
        Guard.NotNull(services);
        services.AddStellaegisOptions<BackplaneServerOptions>(configure ?? (_ => { }));
        return Server(services);
    }

    private static IServiceCollection Server(IServiceCollection services)
    {
        services.AddSignalR();
        services.AddStellaegisStartupCheck();
        services.TryAddSingleton<IBackplaneMembershipPolicy, AuthenticatedMembershipPolicy>();
        services.TryAddSingleton<EventRelay>();
        services.AddHostedService<EventCleanupService>();
        return services;
    }

    /// <summary>
    /// Maps the hub at <paramref name="path"/>, <c>/backplane/hub</c> unless given, and none secured: the host
    /// attaches its authorization to the returned builder, and the startup check fails a host that declares none.
    /// <code>
    /// app.MapBackplaneHub().RequireAuthorization();
    /// </code>
    /// </summary>
    public static HubEndpointConventionBuilder MapBackplaneHub(this IEndpointRouteBuilder endpoints, string path = "/backplane/hub")
    {
        Guard.NotNull(endpoints);
        Guard.NotWhiteSpace(path);
        return endpoints.MapHub<BackplaneHub>(path).ActsOnSpaces().LoadsNoResource();
    }

    /// <summary>
    /// Maps the HTTP routes of the backplane under <paramref name="path"/>, <c>/backplane</c> unless given, and
    /// none secured: <c>POST events</c> publishes an envelope as the hub does, for a service whose connection is
    /// down; <c>GET events?since=&amp;recipients=</c> replays, from the journal, what was published since a
    /// moment to the recipients named — comma-separated segments — and to everyone.
    /// <code>
    /// app.MapBackplaneEndpoints().RequireAuthorization();
    /// </code>
    /// </summary>
    public static RouteGroupBuilder MapBackplaneEndpoints(this IEndpointRouteBuilder endpoints, string path = "/backplane")
    {
        Guard.NotNull(endpoints);
        Guard.NotWhiteSpace(path);
        var group = endpoints.MapGroup(path).BelongsTo<BackplaneModule>().ActsOnSpaces().LoadsNoResource().AddErrorTranslation();

        group.MapPost("/events", async (Envelope envelope, HttpContext http, EventRelay relay, CancellationToken ct) =>
                TypedResults.Accepted((string?)null, await relay.RelayAsync(envelope, http.User, ct).ConfigureAwait(false)))
            .WithMetadata(BackplaneOperations.Publish);

        group.MapGet("/events", async (DateTimeOffset since, string? recipients, IServiceProvider services, CancellationToken ct) =>
            {
                if (services.GetService<IEventJournal>() is not { } journal)
                    throw new UserFacingException(new Error("Journal.Absent", "The host keeps no journal of the events: nothing replays.", ErrorType.PreconditionFailed));
                var replayed = new List<Envelope>();
                await foreach (var stored in journal.ReadAsync(since, ParseRecipients(recipients), ct).ConfigureAwait(false))
                    replayed.Add(stored.Envelope);
                return TypedResults.Ok(replayed);
            })
            .WithMetadata(BackplaneOperations.Replay);

        return group;
    }

    private static List<Recipient> ParseRecipients(string? recipients)
    {
        var parsed = new List<Recipient>();
        foreach (var segment in (recipients ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Recipient.TryParse(segment, out var recipient))
                throw new UserFacingException(new Error("Recipient.Invalid", $"'{segment}' is not a recipient.", ErrorType.Validation));
            parsed.Add(recipient);
        }
        return parsed;
    }
}
