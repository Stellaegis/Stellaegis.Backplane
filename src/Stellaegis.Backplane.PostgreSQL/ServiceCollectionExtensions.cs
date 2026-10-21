using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;
using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.PostgreSQL;

/// <summary>Registers the journal on PostgreSQL in the hub's host.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>The configuration section of the journal's options, <c>Stellaegis:Backplane:PostgreSQL</c>.</summary>
    public const string SectionName = "Stellaegis:Backplane:PostgreSQL";

    /// <summary>
    /// Registers <see cref="PostgreSqlEventJournal"/> as the <see cref="IEventJournal"/> of the hub, over its own
    /// data source, with its options bound from <paramref name="sectionName"/> and validated at startup, and the
    /// journal's database declared for its verification (<see cref="BackplaneDatabase"/>). Call it beside
    /// <c>AddStellaegisBackplaneServer</c>.
    /// </summary>
    public static IServiceCollection AddStellaegisBackplanePostgreSQL(this IServiceCollection services, IConfiguration configuration, string sectionName = SectionName)
    {
        Guard.NotNull(services);
        Guard.NotNull(configuration);
        services.AddStellaegisOptions<PostgreSqlBackplaneOptions>(configuration, sectionName);
        return Journal(services);
    }

    /// <summary>
    /// Registers the journal as <see cref="AddStellaegisBackplanePostgreSQL(IServiceCollection, IConfiguration, string)"/>
    /// does, with its options configured in code.
    /// </summary>
    public static IServiceCollection AddStellaegisBackplanePostgreSQL(this IServiceCollection services, Action<PostgreSqlBackplaneOptions> configure)
    {
        Guard.NotNull(services);
        Guard.NotNull(configure);
        services.AddStellaegisOptions(configure);
        return Journal(services);
    }

    private static IServiceCollection Journal(IServiceCollection services)
    {
        services.TryAddSingleton(provider => new PostgreSqlEventJournal(
            NpgsqlDataSource.Create(provider.GetRequiredService<IOptions<PostgreSqlBackplaneOptions>>().Value.ConnectionString)));
        services.TryAddSingleton<IEventJournal>(provider => provider.GetRequiredService<PostgreSqlEventJournal>());
        services.AddStellaegisModuleDatabase(BackplaneDatabase.Value, OpenAsApplicationAsync);
        return services;
    }

    // The verification opens the journal as the host does: under the connection string of the options.
    private static async Task<DbConnection> OpenAsApplicationAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(services.GetRequiredService<IOptions<PostgreSqlBackplaneOptions>>().Value.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
