using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane.PostgreSQL;

/// <summary>
/// The journal in <c>bp.event_log</c>: each event the hub relayed, as it stamped it, read back by a replay for
/// the recipients a service serves and for everyone, and deleted once older than the retention.
/// </summary>
public sealed class PostgreSqlEventJournal(NpgsqlDataSource dataSource) : IEventJournal
{
    private const string Table = "bp.event_log";

    private readonly NpgsqlDataSource _dataSource = Guard.NotNull(dataSource);

    /// <inheritdoc />
    public async Task AppendAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(envelope);
        if (envelope.Emitter is null)
            throw new ArgumentException("The journal keeps stamped events only: the emitter is missing.", nameof(envelope));

        await using var command = _dataSource.CreateCommand(
            $"insert into {Table} (name, payload, recipient, emitter, published_at) values (@name, @payload, @recipient, @emitter, @published_at)");
        command.Parameters.AddWithValue("name", envelope.Name);
        command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = envelope.Payload.GetRawText() });
        command.Parameters.AddWithValue("recipient", envelope.Recipient);
        command.Parameters.AddWithValue("emitter", envelope.Emitter);
        command.Parameters.AddWithValue("published_at", envelope.PublishedAt.ToUniversalTime());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StoredEvent> ReadAsync(
        DateTimeOffset since, IReadOnlyCollection<Recipient> recipients, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Guard.NotNull(recipients);
        await using var command = _dataSource.CreateCommand(
            $"""
            select id, name, payload::text, recipient, emitter, published_at
            from {Table}
            where published_at > @since and (recipient = @all or recipient = any(@recipients))
            order by id
            """);
        command.Parameters.AddWithValue("since", since.ToUniversalTime());
        command.Parameters.AddWithValue("all", Recipient.AllSegment);
        command.Parameters.AddWithValue("recipients", recipients.Select(recipient => recipient.Segment).ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var payload = JsonDocument.Parse(reader.GetString(2));
            yield return new StoredEvent(
                reader.GetInt64(0),
                new Envelope(reader.GetString(1), payload.RootElement.Clone(), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTime>(5)));
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand($"delete from {Table} where published_at < @before");
        command.Parameters.AddWithValue("before", before.ToUniversalTime());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
