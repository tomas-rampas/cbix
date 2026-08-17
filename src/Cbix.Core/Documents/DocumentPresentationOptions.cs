namespace Cbix.Core.Documents;

/// <summary>
/// The configured provider's document-presentation capability - the single input
/// <see cref="DocumentContentProfileSelector"/> selects a profile from.
/// </summary>
/// <remarks>
/// <para>
/// A type of its own rather than a bare enum in the container, so the registration reads as a
/// configured fact and cannot be confused with any other enum somebody registers later.
/// </para>
/// <para>
/// <b>Bound by the host, never here.</b> Reading it from <c>IConfiguration</c> would put
/// <c>Microsoft.Extensions.Configuration.Abstractions</c> into Core's neutral reference set for one
/// scalar, and - more to the point - which provider a deployment runs is a host decision by design.
/// The host parses its configuration and registers this; Core only consumes it.
/// </para>
/// </remarks>
/// <param name="Capability">
/// What the configured provider can be shown a document as. Defaults to
/// <see cref="DocumentPresentationCapability.NativeDocument"/> - see the remarks on that value for
/// why the fully capable presentation is the safe default rather than the most conservative one.
/// </param>
public sealed record DocumentPresentationOptions(
    DocumentPresentationCapability Capability = DocumentPresentationCapability.NativeDocument);
