using Stellaegis.Primitives.Hosting;

namespace Stellaegis.Backplane.PostgreSQL;

/// <summary>
/// The options of the journal, bound from <c>Stellaegis:Backplane:PostgreSQL</c>: the connection string the
/// hub's host reaches the journal with, under its application role.
/// <code>
/// Stellaegis:Backplane:PostgreSQL:ConnectionString = Host=...;Database=...;Username=...
/// </code>
/// </summary>
public sealed class PostgreSqlBackplaneOptions : IValidatableOptions
{
    /// <summary>The connection string to the database holding the <c>bp</c> schema, under the application's role.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <inheritdoc />
    public string? FindError() => string.IsNullOrWhiteSpace(ConnectionString) ? "ConnectionString is required." : null;
}
