namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/DocumentContentProviderPort.feature</c> (story S01-04).
/// <para>
/// The scenario inspects the port by reflection rather than by referencing it directly,
/// so that the red phase is a genuine assertion failure ("interface not found") instead
/// of a compile error that would stop the test project building at all.
/// </para>
/// </summary>
public sealed class DocumentContentProviderPortState
{
    /// <summary>The reflected <c>IDocumentContentProvider</c> interface, or <see langword="null"/> when it does not exist yet.</summary>
    public Type? PortInterface { get; set; }

    /// <summary>
    /// Every type mentioned by the port's method signatures: parameter types and return
    /// types, with generic arguments, array element types and by-ref element types walked
    /// through, so a provider-specific type cannot hide inside <c>Task&lt;...&gt;</c>.
    /// </summary>
    public IReadOnlyList<Type> SignatureTypes { get; set; } = [];

    /// <summary>
    /// The full surface a caller can reach from those signatures: <see cref="SignatureTypes"/> plus
    /// the public property graph of every non-BCL type among them, walked transitively. This is
    /// what the allowlist is checked against, so a provider type held as a property of a returned
    /// object is caught rather than only one named directly in a signature.
    /// </summary>
    public IReadOnlyList<Type> SurfaceTypes { get; set; } = [];

    /// <summary>
    /// The payload the port hands back, with any awaitable wrapper (<c>Task&lt;T&gt;</c> or
    /// <c>ValueTask&lt;T&gt;</c>) unwrapped to <c>T</c>.
    /// </summary>
    public Type? ReturnPayloadType { get; set; }
}
