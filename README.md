# Stellaegis.Backplane

Stellaegis.Backplane is the internal event bus of a .NET application assembled from several hosts — an
API, a worker, a dedicated service — that must learn from one another what happened: a user was
blocked, a document was indexed, a secret was rotated. A module publishes an event through the
`IEventPublisher` of `Stellaegis.Primitives`, without knowing who listens; the host declares each event
type with its name on the wire and the recipient it goes to; a hub relays it to the hosts serving that
recipient; and each host hands it to the subscribers it took at startup. A single host keeps the same
bus in its process, and nothing leaves it.

The backplane is a package of composition, not a module with data: it acts in no space, it routes
events to their recipients. Those recipients are the compartments of the application, every connected
host, or a named service.

The library targets .NET 10 and is released under the MIT license.

## Packages

| Package | Contents | Depends on |
| --- | --- | --- |
| `Stellaegis.Backplane` | The registry of the events a host declares, `Recipient`, the `Envelope` on the wire, `IEventBus` for the host's subscriptions, the in-process bus, the journal port and its in-memory form | `Stellaegis.Primitives`, `Stellaegis.Primitives.Hosting`, Microsoft.Extensions abstractions |
| `Stellaegis.Backplane.Server` | The SignalR hub mapped in the application's host, under its authentication, the relay that stamps each event with the identity of its publisher, the HTTP routes of publication and replay, the cleanup of the journal | `Stellaegis.Primitives.AspNetCore`, ASP.NET Core |
| `Stellaegis.Backplane.Client` | The bus of a host reaching the hub: connection, queue, HTTP fallback, replay after a reconnection | Microsoft.AspNetCore.SignalR.Client |
| `Stellaegis.Backplane.PostgreSQL` | The journal in the `bp` schema, and the declaration of its database for installation and verification | Npgsql, `Stellaegis.Primitives.Data` |

## How an event travels

1. A module calls `IEventPublisher.PublishAsync(event)`. The event is a record implementing `IEvent`,
   declared by the module that emits it; it carries its compartment when it has one.
2. The bus wraps it in an `Envelope`: the name the host registered for its type, its payload as JSON,
   the segment of its recipient as the host's registration names it. An event of a type the host did
   not register fails: nothing is relayed that was not declared.
3. In a single host, the in-process bus hands the envelope to the subscribers of the process, outside
   the publisher's call. In several, the client sends it to the hub over its connection, or over HTTP
   while the connection is down.
4. The hub stamps the envelope with the identity of the connection that published it and with its
   clock — a service cannot speak in the name of another, and no event carries `system` —, journals it
   when the host keeps a journal, and relays it to the connections of its recipient: the group of a
   compartment, the group of a named service, or every connection.
5. Each host receiving it hands it to its subscribers, in the order of arrival, outside the
   connection's handler. A subscriber that fails is logged, and the next one runs.
6. After a reconnection, a host joins its recipients again and replays, from the journal, what was
   published to them meanwhile.

A host receives its own events as any other, when they go to a recipient it serves or to everyone:
nothing is delivered locally without passing through the hub, so that every host sees one order.

## Declaring the events

Every host of the application registers the events it publishes or subscribes to, with their names on
the wire and the recipient of each, once:

```csharp
builder.Services.AddStellaegisBackplane(b => b
    .Register<InvoiceIssued>("invoice.issued", e => Recipient.Of(e.Compartment))
    .Register<UserBlocked>("user.blocked", _ => Recipient.All)
    .Register<VaultRotated>("vault.rotated", _ => Recipient.Service("identity")));
```

A name is identifiers joined by dots, `invoice.issued`; it is the contract that stabilizes, where the
.NET type may move. A name registered twice, or a type registered twice, fails the startup. The recipient
is declared per type, and there is no default: an event of the shared space goes where its registration
says, never to a recipient derived from its space.

`AddStellaegisBackplane` declares the backplane module to the application, without compartments and with
its two operations, `events.publish` and `events.replay`, so that it fits every frame.

## Subscribing

The host subscribes at startup, through `IEventBus`; a module never subscribes, it publishes:

```csharp
var bus = app.Services.GetRequiredService<IEventBus>();
bus.Subscribe<UserBlocked>((e, ct) => sessions.RevokeAsync(e.UserId, ct));
bus.SubscribeToDeliveries<InvoiceIssued>((d, ct) => audit.RecordAsync(d.Event, d.Emitter, d.PublishedAt, ct));
bus.Subscribe("documents.indexed", (raw, ct) => search.RefreshAsync(raw.Payload, ct));
```

The second form gives the delivery around the event: its recipient, its emitter as the hub stamped it, the
moment it was published. The third subscribes by name, as JSON, to an event of another service whose type
this host does not have: its contract is its name.

## A single host

```csharp
builder.Services
    .AddStellaegisBackplane(b => ...)
    .AddStellaegisInMemoryBackplane(emitter: Actor.Parse("api"));
```

The in-process bus is the `IEventPublisher` of the modules and the `IEventBus` of the host. The emitter it
stamps is the identity the host gives it, or nothing. Publication returns once the event is queued.

## Several hosts: the hub

One host of the application maps the hub, under its own authentication — the package registers no scheme
of its own, so that a single-process application and a dedicated host map it the same way:

```csharp
builder.Services
    .AddStellaegisBackplane(b => ...)
    .AddStellaegisBackplaneServer<TrustedServices>(builder.Configuration)
    .AddStellaegisBackplanePostgreSQL(builder.Configuration);   // optional: the journal, for replay

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapBackplaneHub().RequireAuthorization();
app.MapBackplaneEndpoints().RequireAuthorization();
await app.Services.VerifyStellaegisDatabasesAsync();            // with the journal
```

The startup check of `Stellaegis.Primitives.AspNetCore` fails a host that maps the hub or the routes
without authorization. Who may connect is that authorization; which recipients a connection may join is
the `IBackplaneMembershipPolicy` of the application, which `AddStellaegisBackplaneServer<TPolicy>` requires:
there is no built-in policy, and refusal is the default. An application whose services are all trusted,
on a private network, writes the policy that says so:

```csharp
sealed class TrustedServices : IBackplaneMembershipPolicy
{
    public Task<bool> CanJoinAsync(ClaimsPrincipal user, Recipient recipient, CancellationToken ct = default)
        => Task.FromResult(user.Identity?.IsAuthenticated == true);
}
```

The hub stamps the emitter from the claims of the connection, in this order: `client_id`, the identifier
of a service authenticated with client credentials; the name identifier; `sub`. A connection with none is
refused on publication.

SignalR reads a bearer token from the `Authorization` header; a WebSocket connection cannot send one, and
a JWT host lets its scheme read `access_token` from the query string on the hub's path:

```csharp
options.Events = new JwtBearerEvents
{
    OnMessageReceived = context =>
    {
        var token = context.Request.Query["access_token"];
        if (!string.IsNullOrEmpty(token) && context.Request.Path.StartsWithSegments("/backplane/hub"))
            context.Token = token;
        return Task.CompletedTask;
    }
};
```

The options of the hub, under `Stellaegis:Backplane:Server`, say how long the journal keeps an event
(`EventRetention`, a day) and how often the expired ones are deleted (`CleanupInterval`, an hour).

## Several hosts: the clients

Every other host, and the hub's host itself when its modules publish, registers the client:

```csharp
builder.Services
    .AddStellaegisBackplane(b => ...)
    .AddSingleton<IBackplaneTokenProvider, ClientCredentialsTokenProvider>()   // if the hub asks a token
    .AddStellaegisBackplaneClient(builder.Configuration);
```

```text
Stellaegis:Backplane:Client:BaseUrl         = https://backplane.internal
Stellaegis:Backplane:Client:ServiceName     = identity          (optional: to receive what is addressed to it)
Stellaegis:Backplane:Client:Compartments:0  = acme              (the compartments this host serves)
Stellaegis:Backplane:Client:QueueCapacity   = 500               (events waiting for the hub; the oldest is dropped beyond)
Stellaegis:Backplane:Client:RetryDelay      = 00:00:05
Stellaegis:Backplane:Client:StartupAttempts = 10
```

The client connects when the host starts, joins the recipients the options name, and fails the startup
when the hub is not reached after the configured attempts: a host whose backplane is unreachable does
not serve. The `IBackplaneTokenProvider` gives the token presented to the hub and to the HTTP routes;
without one, the client presents none.

## The journal

The hub relays and forgets, unless its host registers an `IEventJournal`. `Stellaegis.Backplane.PostgreSQL`
keeps each relayed event in `bp.event_log`, under `Stellaegis:Backplane:PostgreSQL:ConnectionString`.
The schema is installed by the installer of the application, under the role that owns it, and verified by
the host at startup under its own role:

```csharp
await DatabaseLifecycle.InstallAsync([BackplaneDatabase.Value], AppFrame.Value, openAsAdmin, "app_role");
```

`AddStellaegisInMemoryEventJournal()` registers a journal in memory, for a single host and for tests.

## Tests

```powershell
$env:STELLAEGIS_TEST_POSTGRESQL = "Host=127.0.0.1;Username=postgres;Database=postgres"
dotnet test --solution Stellaegis.Backplane.slnx
```

Without the variable, the journal tests are skipped. The publishing workflow runs them against a
PostgreSQL service and fails on a skipped test.
