# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [5.0.0] — 2026-10-21

First release of the package, extracted from the backplane of the Stellaegis framework and rewritten on
`Stellaegis.Primitives` 5.0.0. It starts at the major version of the family.

### What it keeps from the framework

- The shape: a hub on SignalR, a client with a bounded queue and an HTTP fallback, a replay since a
  moment, a journal on PostgreSQL in `bp.event_log` with its cleanup.
- The idea of a recipient: the compartment of an event, or everyone.

### What changes

- The registry of events is an instance filled by the host, `AddStellaegisBackplane(b => b.Register<T>(name,
  recipient))`, in place of the static registry. A name and a type are registered once; an unregistered type
  is refused at publication.
- The recipient is declared per type, with no default, as a `Recipient`: a compartment, everyone (`_all`), or a
  named service (`service:name`). `TenantScope` leaves.
- The emitter is stamped by the hub from the identity of the connection — `client_id`, the name identifier or
  `sub` — and a connection without identity is refused: no event is published by `system`.
- The routing by recipient is written: a client joins the groups of the recipients it serves, the hub
  publishes to the group or to everyone, the HTTP publication and the replay follow the same routing.
- The envelope travels as JSON, not as JSON in a string, and is the one shape of the hub, of the routes and of
  the journal.
- The hub lives under the authentication of the host that maps it, never under a bearer scheme of its own, and
  the startup check refuses it without authorization. The application's `IBackplaneMembershipPolicy`, required
  at registration, decides which recipients a connection joins; there is no built-in policy.
- The subscriptions of a host go through `IEventBus`, by type, by type with its delivery, or by name as JSON;
  the publication of a module goes through the `IEventPublisher` of Primitives.
- The journal is a port, `IEventJournal`, with a form in memory; the PostgreSQL form declares its database
  through `ModuleDatabase`, installed by `DatabaseLifecycle` and verified at startup. The SQL script leaves
  `docs/sql`.
- The options are validated when the host starts; the HTTP client of the routes comes from
  `AddStellaegisHttpClient`; a host whose hub is unreachable refuses to start.
- LISTEN/NOTIFY is not carried over: the framework listened without anyone notifying. A second hub will bring
  it back whole.
- The twelve events of the framework's socle stay with the modules that emit them.
