using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.Server;

/// <summary>
/// The options of the hub, bound from <c>Stellaegis:Backplane:Server</c>: how long the journal keeps an event
/// for replay, and how often the expired ones are deleted.
/// <code>
/// Stellaegis:Backplane:Server:EventRetention = 1.00:00:00
/// Stellaegis:Backplane:Server:CleanupInterval = 01:00:00
/// </code>
/// </summary>
public sealed class BackplaneServerOptions : IValidatableOptions
{
    /// <summary>How long an event stays in the journal. A day unless set.</summary>
    public TimeSpan EventRetention { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often the expired events are deleted. An hour unless set.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public string? FindError()
    {
        if (EventRetention <= TimeSpan.Zero)
            return "EventRetention must be positive.";
        if (CleanupInterval <= TimeSpan.Zero)
            return "CleanupInterval must be positive.";
        return null;
    }
}

/// <summary>Deletes from the journal, at each interval, the events older than the retention. Idle without a journal.</summary>
internal sealed class EventCleanupService(IServiceProvider services, IOptions<BackplaneServerOptions> options, ILogger<EventCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService(typeof(IEventJournal)) is not IEventJournal journal)
            return;
        using var timer = new PeriodicTimer(options.Value.CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var deleted = await journal.DeleteBeforeAsync(DateTimeOffset.UtcNow - options.Value.EventRetention, stoppingToken).ConfigureAwait(false);
                if (deleted > 0)
                    logger.LogInformation("Deleted {Count} expired events from the backplane journal.", deleted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The cleanup of the backplane journal failed; it runs again at the next interval.");
            }
        }
    }
}
