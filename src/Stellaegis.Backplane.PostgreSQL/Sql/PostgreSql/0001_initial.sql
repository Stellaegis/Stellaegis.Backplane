-- ============================================================================
-- Stellaegis.Backplane — the journal schema, script 0001
-- ============================================================================
-- Applied by the installation of the application (DatabaseLifecycle.InstallAsync of
-- Stellaegis.Primitives.Data), under the role that owns the schema; the schema is created by the
-- installation before this script. The application connects under an ordinary role, granted its
-- data rights by the installation.
--
-- No row-level security: the journal is reached by the hub's host only, never in a space of a
-- request, and the recipient of a row is what the replay filters on.
-- ============================================================================

-- The events the hub relayed, as it stamped them: one row per event, in the order of relaying.
--
-- recipient: the segment of the recipient — a compartment identifier, _all for everyone, or
--            service:{name} for a named service —, as Stellaegis.Backplane.Recipient writes it.
-- emitter:   the identity of the service that published, stamped by the hub from its connection;
--            never a default, since an event always says which service published it.

create table bp.event_log (
  id           bigint      generated always as identity,
  name         text        not null,
  payload      jsonb       not null,
  recipient    text        not null,
  emitter      text        not null,
  published_at timestamptz not null,

  constraint pk_bp_event_log primary key (id)
);

comment on table  bp.event_log              is 'The events the hub relayed, kept for replay and deleted after their retention.';
comment on column bp.event_log.name         is 'The name of the event on the wire, identifiers joined by dots (e.g. invoice.issued).';
comment on column bp.event_log.recipient    is 'The segment of the recipient: a compartment, _all, or service:{name}.';
comment on column bp.event_log.emitter      is 'The identity of the service that published, stamped by the hub.';
comment on column bp.event_log.published_at is 'When the hub relayed the event; what a replay reads since.';

-- A replay reads what was published since a moment; the cleanup deletes what is older than the retention.
create index ix_bp_event_log_published_at
  on bp.event_log (published_at);
