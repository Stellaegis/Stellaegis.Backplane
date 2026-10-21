using Stellaegis.Primitives;

namespace Stellaegis.Backplane;

/// <summary>
/// The identity of the backplane. It is a package of composition, not a module with compartments: it acts in no
/// space, it routes events to their recipients. Declared without compartments, so that it fits every frame.
/// </summary>
public sealed class BackplaneModule : IStellaegisModule
{
    /// <inheritdoc />
    public static string Name => "backplane";
}

/// <summary>
/// The operations of the backplane, on the routes a host maps: publishing an event and replaying the journal.
/// Who may call them is the host's decision — a backplane serves trusted services on a private network.
/// </summary>
public static class BackplaneOperations
{
    /// <summary>Publishing an event to its recipients.</summary>
    public static readonly Operation Publish = new("events", "publish", OperationKind.Write);

    /// <summary>Reading the events published since a moment, for the recipients the caller serves.</summary>
    public static readonly Operation Replay = new("events", "replay", OperationKind.Read);

    /// <summary>Every operation, as the module declares them.</summary>
    public static readonly IReadOnlyList<Operation> All = [Publish, Replay];
}
