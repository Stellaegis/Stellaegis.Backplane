using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane;

/// <summary>
/// Registers the backplane: the events a host declares, the module, and the in-process bus of a single host.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Declares the events of the host to the backplane and the backplane module to the application. Called once
    /// per host; a transport is registered beside it — <c>AddStellaegisInMemoryBackplane</c> for a single host,
    /// the client of a hub for several.
    /// <code>
    /// services.AddStellaegisBackplane(b => b
    ///     .Register&lt;InvoiceIssued&gt;("invoice.issued", e => Recipient.Of(e.Compartment))
    ///     .Register&lt;UserBlocked&gt;("user.blocked", _ => Recipient.All));
    /// </code>
    /// </summary>
    /// <exception cref="InvalidOperationException">The backplane is already registered, or an event twice.</exception>
    public static IServiceCollection AddStellaegisBackplane(this IServiceCollection services, Action<BackplaneBuilder> configure)
    {
        Guard.NotNull(services);
        Guard.NotNull(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(EventRegistry)))
            throw new InvalidOperationException("The backplane is already registered: a host declares its events once.");

        var builder = new BackplaneBuilder();
        configure(builder);
        services.AddSingleton(builder.Build());
        services.AddStellaegisModule(ModuleDeclaration.WithoutCompartments<BackplaneModule>().WithOperations(BackplaneOperations.All));
        return services;
    }

    /// <summary>
    /// Registers the in-process bus as the <see cref="IEventPublisher"/> of the modules and the <see cref="IEventBus"/>
    /// of the host, stamping <paramref name="emitter"/> on each delivery when given — the identity of the host, as
    /// a hub would stamp it. For a single host: nothing leaves the process.
    /// </summary>
    public static IServiceCollection AddStellaegisInMemoryBackplane(this IServiceCollection services, Actor? emitter = null)
    {
        Guard.NotNull(services);
        services.TryAddSingleton(provider => new InMemoryBackplane(
            provider.GetRequiredService<EventRegistry>(), provider.GetRequiredService<ILogger<InMemoryBackplane>>(), emitter));
        services.TryAddSingleton<IEventPublisher>(provider => provider.GetRequiredService<InMemoryBackplane>());
        services.TryAddSingleton<IEventBus>(provider => provider.GetRequiredService<InMemoryBackplane>());
        return services;
    }

    /// <summary>Registers the journal in memory, for a single host and for tests.</summary>
    public static IServiceCollection AddStellaegisInMemoryEventJournal(this IServiceCollection services)
    {
        Guard.NotNull(services);
        services.TryAddSingleton<IEventJournal, InMemoryEventJournal>();
        return services;
    }
}
