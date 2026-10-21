using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane;

/// <summary>An event the journal holds, with the position it was appended at.</summary>
public sealed record StoredEvent(long Id, Envelope Envelope);

/// <summary>
/// The journal of the events a hub relayed, for the replay a service asks for after a disconnection: what was
/// published since a moment, for the recipients it serves. A hub without a journal relays and forgets.
/// </summary>
public interface IEventJournal
{
    /// <summary>Appends <paramref name="envelope"/>, as the hub stamped it.</summary>
    Task AppendAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The events published strictly after <paramref name="since"/> whose recipient is one of
    /// <paramref name="recipients"/> or everyone, in the order of appending.
    /// </summary>
    IAsyncEnumerable<StoredEvent> ReadAsync(DateTimeOffset since, IReadOnlyCollection<Recipient> recipients, CancellationToken cancellationToken = default);

    /// <summary>Deletes the events published before <paramref name="before"/>, and returns how many.</summary>
    Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default);
}

/// <summary>A journal in memory, for a single host and for tests: lost with the process.</summary>
public sealed class InMemoryEventJournal : IEventJournal
{
    private readonly ConcurrentQueue<StoredEvent> _events = new();
    private long _next;

    /// <inheritdoc />
    public Task AppendAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(envelope);
        _events.Enqueue(new StoredEvent(Interlocked.Increment(ref _next), envelope));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StoredEvent> ReadAsync(
        DateTimeOffset since, IReadOnlyCollection<Recipient> recipients, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Guard.NotNull(recipients);
        var segments = recipients.Select(recipient => recipient.Segment).ToHashSet(StringComparer.Ordinal);
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var stored in _events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stored.Envelope.PublishedAt > since && (stored.Envelope.Recipient == Recipient.AllSegment || segments.Contains(stored.Envelope.Recipient)))
                yield return stored;
        }
    }

    /// <inheritdoc />
    public Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        while (_events.TryPeek(out var oldest) && oldest.Envelope.PublishedAt < before && _events.TryDequeue(out _))
            deleted++;
        return Task.FromResult(deleted);
    }
}
