using System.Diagnostics.CodeAnalysis;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane;

/// <summary>
/// Who an event goes to: every connected service, the services serving one compartment, or one named service. The
/// host declares the recipient of each event type when it registers it; nothing is relayed by default.
/// <code>
/// Recipient.All                                   // _all
/// Recipient.Of(Compartment.Parse("acme"))         // acme
/// Recipient.Service("identity")                   // service:identity
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// A recipient has a segment, written on the wire and in the journal: the compartment identifier, <c>_all</c>,
/// which no compartment can carry since the rule refuses the underscore, or <c>service:</c> followed by an
/// identifier. Nothing reads a space back from a segment: the recipient of an event of the shared space is
/// whatever its registration names, never a value derived from the space.
/// </para>
/// <para>Two recipients are equal when their segments are.</para>
/// </remarks>
public sealed class Recipient : IEquatable<Recipient>
{
    /// <summary>The segment of <see cref="All"/>, <c>_all</c>.</summary>
    public const string AllSegment = "_all";

    /// <summary>The prefix of a named service, <c>service:</c>.</summary>
    public const string ServicePrefix = "service:";

    private Recipient(string segment, Compartment? compartment, string? serviceName)
    {
        Segment = segment;
        Compartment = compartment;
        ServiceName = serviceName;
    }

    /// <summary>Every connected service.</summary>
    public static Recipient All { get; } = new(AllSegment, null, null);

    /// <summary>The services serving <paramref name="compartment"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="compartment"/> is <see langword="null"/>.</exception>
    public static Recipient Of(Compartment compartment) => new(Guard.NotNull(compartment).Value, compartment, null);

    /// <summary>The service named <paramref name="name"/>, an identifier such as <c>identity</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid identifier.</exception>
    public static Recipient Service(string name)
    {
        var serviceName = Guard.Satisfies(name, NameRules.IsIdentifier, $"'{name}' is not a valid service name: {NameRules.IdentifierRule}.");
        return new(ServicePrefix + serviceName, null, serviceName);
    }

    /// <summary>The segment on the wire and in the journal.</summary>
    public string Segment { get; }

    /// <summary>The compartment, or <see langword="null"/> unless the recipient is one.</summary>
    public Compartment? Compartment { get; }

    /// <summary>The service name, or <see langword="null"/> unless the recipient is one.</summary>
    public string? ServiceName { get; }

    /// <summary>Whether the recipient is every connected service.</summary>
    public bool IsAll => Compartment is null && ServiceName is null;

    /// <summary>Reads a recipient the code knows to be valid.</summary>
    /// <exception cref="FormatException"><paramref name="segment"/> is not a recipient segment.</exception>
    public static Recipient Parse(string segment)
        => TryParse(Guard.NotNull(segment), out var recipient)
            ? recipient
            : throw new FormatException($"'{segment}' is not a recipient: a compartment, {AllSegment}, or {ServicePrefix} followed by an identifier.");

    /// <summary>Reads a recipient from an input that may be invalid, such as a value on the wire.</summary>
    public static bool TryParse([NotNullWhen(true)] string? segment, [MaybeNullWhen(false)] out Recipient result)
    {
        result = null;
        if (segment is null)
            return false;
        if (segment == AllSegment)
            result = All;
        else if (segment.StartsWith(ServicePrefix, StringComparison.Ordinal) && NameRules.IsIdentifier(segment[ServicePrefix.Length..]))
            result = new Recipient(segment, null, segment[ServicePrefix.Length..]);
        else if (Primitives.Compartment.TryParse(segment, out var compartment))
            result = new Recipient(segment, compartment, null);
        return result is not null;
    }

    /// <inheritdoc />
    public bool Equals(Recipient? other) => other is not null && string.Equals(Segment, other.Segment, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Recipient r && Equals(r);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Segment);

    /// <summary>Value equality.</summary>
    public static bool operator ==(Recipient? left, Recipient? right) => left is null ? right is null : left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(Recipient? left, Recipient? right) => !(left == right);

    /// <summary>The segment.</summary>
    public override string ToString() => Segment;
}
