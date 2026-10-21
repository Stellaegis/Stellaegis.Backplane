using Stellaegis.Primitives;

namespace Stellaegis.Backplane.Tests;

/// <summary>The events of the tests: one of a compartment, one of everyone, one of a named service.</summary>
public sealed record WidgetChanged(Compartment Compartment, Guid WidgetId, DateTimeOffset OccurredAt) : IEvent;

public sealed record UserBlocked(string UserId, DateTimeOffset OccurredAt) : IEvent;

public sealed record VaultRotated(int Slot, DateTimeOffset OccurredAt) : IEvent;

/// <summary>An event never registered, to prove the refusal.</summary>
public sealed record Unregistered(DateTimeOffset OccurredAt) : IEvent;

internal static class Events
{
    public static readonly Compartment Acme = Compartment.Parse("acme");
    public static readonly Compartment Globex = Compartment.Parse("globex");

    public static BackplaneBuilder RegisterAll(BackplaneBuilder builder) => builder
        .Register<WidgetChanged>("widgets.changed", e => Recipient.Of(e.Compartment))
        .Register<UserBlocked>("users.blocked", _ => Recipient.All)
        .Register<VaultRotated>("vault.rotated", _ => Recipient.Service("identity"));
}
