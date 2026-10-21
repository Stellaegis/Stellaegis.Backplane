using Stellaegis.Primitives.Data;

namespace Stellaegis.Backplane.PostgreSQL;

/// <summary>
/// What the PostgreSQL journal declares about its database: the schema <c>bp</c> and its numbered scripts. An
/// installer applies it under the role that owns the schema (<see cref="DatabaseLifecycle.InstallAsync"/>); the
/// hub's host verifies it at startup under its own role (<see cref="DatabaseLifecycle.VerifyAsync"/>, registered
/// by <c>AddStellaegisBackplanePostgreSQL</c>).
/// </summary>
public static class BackplaneDatabase
{
    /// <summary>The schema holding the journal, <c>bp</c>.</summary>
    public const string Schema = "bp";

    /// <summary>The declaration of the journal's database.</summary>
    public static ModuleDatabase Value { get; } = ModuleDatabase.PostgreSql<BackplaneModule>(Schema)
        .WithScripts(SqlScripts.FromResources(typeof(BackplaneDatabase).Assembly, "Stellaegis.Backplane.PostgreSQL.Sql.PostgreSql"));
}
