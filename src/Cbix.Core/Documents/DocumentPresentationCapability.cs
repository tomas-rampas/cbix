namespace Cbix.Core.Documents;

/// <summary>
/// What the configured provider can be shown a document as - the capability the active
/// <see cref="IDocumentContentProvider"/> profile is selected from (design 5.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a statement about the provider, not a choice of profile.</b> Configuration says what
/// the provider can accept; <see cref="DocumentContentProfileSelector"/> maps that onto whichever
/// profile implements it. The distinction is the point of design 5.1's "only the strategy
/// implementations know which profile is in play": an operator configures a capability, not a class
/// name, so adding a fourth profile later changes no configuration and no call site.
/// </para>
/// <para>
/// <b>The values are ordered by how much of the document survives the trip</b>, and that ordering is
/// load-bearing for the PoC's headline metric. The permission matrix is a visual table: read
/// natively it is read as laid out, read from page images it is read as pixels, and read from the
/// text layer alone its column structure is gone. A deployment that drops to
/// <see cref="TextOnly"/> is running degraded, which <see cref="DocumentContentCapabilities.IsDegraded"/>
/// reports rather than leaves to be inferred.
/// </para>
/// </remarks>
public enum DocumentPresentationCapability
{
    /// <summary>
    /// The provider ingests the document file itself - Claude's native PDF mode, reached through the
    /// Files API with <c>cache_control</c> set (story S01-05).
    /// </summary>
    /// <remarks>
    /// <para>
    /// First value, so it is also the <see langword="default"/>, and the honest argument for that is
    /// not the one first written here. That version claimed guessing wrong in this direction produces
    /// "a provider refusing a document block loudly" - which is false: a provider that cannot accept a
    /// document simply is not configured, and the more likely wrong-direction outcome is a document
    /// leaving the estate that a cautious operator did not intend to send. That is not a loud failure
    /// at all.
    /// </para>
    /// <para>
    /// The real argument is that the egress decision has already been made explicitly upstream. This
    /// value cannot cause a document to be uploaded anywhere unless a provider API key was
    /// deliberately provisioned through the secrets manager, and the worker refuses to start without
    /// one - so an unconfigured presentation never means an unconfigured destination. What was missing
    /// was the record, and the host now emits a startup event naming the presentation mode alongside
    /// the one naming the endpoint, so the decision is stated in the log of every run rather than
    /// inferred from a default.
    /// </para>
    /// <para>
    /// Given that, defaulting to the fully capable presentation is right for the reason that survives
    /// scrutiny: the other direction degrades extraction quality silently, and matrix cell accuracy is
    /// the PoC's headline metric.
    /// </para>
    /// </remarks>
    NativeDocument = 0,

    /// <summary>
    /// The provider accepts images but not documents: the text layer plus locally rendered page
    /// images, sent as ordinary multimodal content (story S01-06).
    /// </summary>
    Vision = 1,

    /// <summary>
    /// The provider accepts text only: the PDFPig text layer alone, explicitly flagged degraded
    /// (story S01-07).
    /// </summary>
    TextOnly = 2,
}
