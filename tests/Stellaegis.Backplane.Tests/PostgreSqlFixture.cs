using Npgsql;
using Stellaegis.Backplane.PostgreSQL;
using Stellaegis.Primitives;
using Stellaegis.Primitives.Data;

namespace Stellaegis.Backplane.Tests;

/// <summary>
/// The journal's schema, <c>bp</c>, reinstalled at every run by the installation of the module
/// (<see cref="DatabaseLifecycle.InstallAsync"/>) under the connection of the variable, which owns it; the tests
/// then connect under an ordinary role, <c>stellaegis_backplane_app</c>, created once, as a host does.
/// </summary>
/// <remarks>
/// The connection string comes from <c>STELLAEGIS_TEST_POSTGRESQL</c>. Without it, <see cref="Unavailable"/> says
/// so and the tests that need the database are skipped; the publication fails a run with a skipped test, so it
/// sets the variable.
/// </remarks>
[CollectionDefinition(PostgreSqlCollection.Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private const string Variable = "STELLAEGIS_TEST_POSTGRESQL";
    private const string AppRole = "stellaegis_backplane_app";

    /// <summary>The connection string of the installer, which owns the schema.</summary>
    public string? AdminConnectionString { get; } = Environment.GetEnvironmentVariable(Variable);

    /// <summary>The connection string the tests use, under the application's role.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Why the database cannot be used, or <c>null</c> when it can.</summary>
    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (AdminConnectionString is null)
        {
            Unavailable = $"No PostgreSQL for the journal tests: set {Variable}, e.g. Host=127.0.0.1;Username=postgres;Database=postgres.";
            return;
        }
        ConnectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Username = AppRole, Password = AppRole }.ConnectionString;
        try
        {
            await using (var connection = new NpgsqlConnection(AdminConnectionString))
            {
                await connection.OpenAsync();
                await using var reset = new NpgsqlCommand($"""
                    drop schema if exists {BackplaneDatabase.Schema} cascade;
                    do $$ begin
                      if not exists (select 1 from pg_roles where rolname = '{AppRole}') then
                        create role {AppRole} login password '{AppRole}';
                      end if;
                    end $$;
                    """, connection);
                await reset.ExecuteNonQueryAsync();
            }

            await DatabaseLifecycle.InstallAsync(
                [BackplaneDatabase.Value], ApplicationFrame.ManyCompartments(), OpenAdminAsync, AppRole, Actor.Job("tests"));
        }
        catch (NpgsqlException e)
        {
            Unavailable = $"No PostgreSQL for the journal tests ({Variable}): {e.Message}";
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<System.Data.Common.DbConnection> OpenAdminAsync(ModuleDatabase _, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
