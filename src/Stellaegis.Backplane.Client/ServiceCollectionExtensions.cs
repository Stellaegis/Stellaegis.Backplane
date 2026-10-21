using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.Client;

/// <summary>Registers the client of the hub as the bus of a host.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The configuration section of the client's options, <c>Stellaegis:Backplane:Client</c>.</summary>
    public const string SectionName = "Stellaegis:Backplane:Client";

    /// <summary>
    /// Registers <see cref="SignalRBackplane"/> as the <see cref="IEventPublisher"/> of the modules and the
    /// <see cref="IEventBus"/> of the host, with its options bound from <paramref name="sectionName"/> and
    /// validated at startup, the HTTP client of the hub's routes, and the hosted service that connects when the
    /// host starts — and refuses to start when the hub is not reached after the configured attempts. Call it
    /// beside <c>AddStellaegisBackplane</c>, which declares the events and the module, and register an
    /// <see cref="IBackplaneTokenProvider"/> before when the hub asks a token.
    /// </summary>
    public static IServiceCollection AddStellaegisBackplaneClient(this IServiceCollection services, IConfiguration configuration, string sectionName = SectionName)
    {
        Guard.NotNull(services);
        Guard.NotNull(configuration);
        services.AddStellaegisHttpClient<IBackplaneEndpoints, BackplaneHttpClient, BackplaneClientOptions>(configuration, sectionName);
        return Client(services);
    }

    /// <summary>
    /// Registers the client as <see cref="AddStellaegisBackplaneClient(IServiceCollection, IConfiguration, string)"/>
    /// does, with its options configured in code.
    /// </summary>
    public static IServiceCollection AddStellaegisBackplaneClient(this IServiceCollection services, Action<BackplaneClientOptions> configure)
    {
        Guard.NotNull(services);
        Guard.NotNull(configure);
        services.AddStellaegisHttpClient<IBackplaneEndpoints, BackplaneHttpClient, BackplaneClientOptions>(configure);
        return Client(services);
    }

    private static IServiceCollection Client(IServiceCollection services)
    {
        services.TryAddSingleton(provider => new SignalRBackplane(
            provider.GetRequiredService<EventRegistry>(),
            provider.GetRequiredService<IOptions<BackplaneClientOptions>>(),
            provider.GetRequiredService<IBackplaneEndpoints>(),
            provider,
            provider.GetRequiredService<ILogger<SignalRBackplane>>()));
        services.TryAddSingleton<IEventPublisher>(provider => provider.GetRequiredService<SignalRBackplane>());
        services.TryAddSingleton<IEventBus>(provider => provider.GetRequiredService<SignalRBackplane>());
        services.AddHostedService<BackplaneClientHostedService>();
        return services;
    }
}

/// <summary>
/// Connects the host to the hub when it starts, trying <see cref="BackplaneClientOptions.StartupAttempts"/>
/// times at <see cref="BackplaneClientOptions.RetryDelay"/>, and fails the startup when none succeeds: a host
/// whose backplane is unreachable does not serve.
/// </summary>
internal sealed class BackplaneClientHostedService(SignalRBackplane backplane, IOptions<BackplaneClientOptions> options, ILogger<BackplaneClientHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var attempts = options.Value.StartupAttempts;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await backplane.ConnectAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Connected to the backplane hub, serving {Recipients}.", string.Join(", ", backplane.Recipients));
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException && attempt < attempts)
            {
                logger.LogWarning(exception, "The backplane hub is not reached (attempt {Attempt} of {Attempts}); retrying in {Delay}.", attempt, attempts, options.Value.RetryDelay);
                await Task.Delay(options.Value.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
