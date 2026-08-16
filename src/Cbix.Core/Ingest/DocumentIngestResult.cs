using Cbix.Core.Documents;

namespace Cbix.Core.Ingest;

/// <summary>
/// What the ingest gate decided about one submission.
/// </summary>
/// <remarks>
/// The workflow reads <see cref="IsNewRegistration"/> to decide whether there is any work left to
/// do: a duplicate stops here, before triage and before a single model call is paid for
/// (design 5.1). Everything a continuing run needs downstream - identity, location, media type -
/// hangs off <see cref="Submitted"/>, and the document preparation ingest paid for on a continuing
/// run - today the local text layer - hangs off <see cref="TextLayer"/>.
/// <para>
/// <b>A class rather than a record, for the reason <see cref="Ingest.TextLayer"/> gives for being one
/// itself.</b> This type holds a <see cref="Ingest.TextLayer"/>, and a record's synthesized
/// <c>Equals</c> would compare that member by reference - so two results describing the same
/// document with character-identical text would report as different, and the compiler would offer
/// value equality it cannot actually deliver. The defect is the same one level up, and inheriting it
/// silently would be worse than the original, because nothing here would say so. Equality has no use
/// on this type anyway: the identity that matters is the content hash on
/// <see cref="Registered"/>.
/// </para>
/// <para>
/// Get-only properties, no <c>init</c> setters: a <c>with</c> expression bypasses the constructor,
/// and the constructor is where the invariants below are enforced.
/// </para>
/// </remarks>
public sealed class DocumentIngestResult
{
    /// <summary>Initialises a new <see cref="DocumentIngestResult"/>.</summary>
    /// <param name="submitted">The document as read on this submission.</param>
    /// <param name="registered">
    /// The registry row now standing for these bytes: this submission's own on a first
    /// registration, the earlier one on a duplicate.
    /// </param>
    /// <param name="isNewRegistration"><see langword="true"/> when this submission created <paramref name="registered"/>.</param>
    /// <param name="textLayer">
    /// The document's local text layer on a first registration; <see langword="null"/> on a
    /// duplicate, which is not prepared because its run does not continue.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="submitted"/> or <paramref name="registered"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="submitted"/> and <paramref name="registered"/> do not share an identity;
    /// <paramref name="textLayer"/> belongs to a different document; or <paramref name="textLayer"/>
    /// does not match <paramref name="isNewRegistration"/>.
    /// </exception>
    /// <remarks>
    /// <b><paramref name="textLayer"/> is a required argument, and both directions of its invariant
    /// are enforced.</b> A registered document is always prepared and a duplicate never is
    /// (design 5.1), so exactly one of the two shapes is valid - and a nullable parameter with a
    /// default would let the wrong one compile silently at the one call site that matters. Making the
    /// caller state it means a future change to that rule has to come here and argue with this check,
    /// rather than slip through as a forgotten argument.
    /// </remarks>
    public DocumentIngestResult(
        DocumentReference submitted,
        DocumentRegistryEntry registered,
        bool isNewRegistration,
        TextLayer? textLayer)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(registered);

        if (!string.Equals(submitted.DocumentId, registered.DocumentId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The submitted document '{submitted.DocumentId}' and the registered entry '{registered.DocumentId}' are different documents.",
                nameof(registered));
        }

        if (isNewRegistration && textLayer is null)
        {
            throw new ArgumentException(
                "A newly registered document must carry the text layer ingest extracted for it: the validator's grounding gate has nothing to check against without one.",
                nameof(textLayer));
        }

        if (!isNewRegistration && textLayer is not null)
        {
            throw new ArgumentException(
                "A duplicate submission must not carry a text layer: its run stops at the registry, so nothing was prepared for it.",
                nameof(textLayer));
        }

        if (textLayer is not null && !string.Equals(textLayer.DocumentId, registered.DocumentId, StringComparison.Ordinal))
        {
            // The text layer is the corpus a published value's provenance is checked against, so a
            // layer belonging to another document would let a snippet be "grounded" in a page that
            // is not the source at all.
            throw new ArgumentException(
                $"The text layer belongs to document '{textLayer.DocumentId}', not to '{registered.DocumentId}'.",
                nameof(textLayer));
        }

        Submitted = submitted;
        Registered = registered;
        IsNewRegistration = isNewRegistration;
        TextLayer = textLayer;
    }

    /// <summary>Gets the reference to the document as read on this submission.</summary>
    public DocumentReference Submitted { get; }

    /// <summary>Gets the registry row standing for these bytes.</summary>
    public DocumentRegistryEntry Registered { get; }

    /// <summary>
    /// Gets a value indicating whether this submission created the registry row. When
    /// <see langword="false"/> the submission was a duplicate and the run does not continue.
    /// </summary>
    public bool IsNewRegistration { get; }

    /// <summary>
    /// Gets the document's local text layer, or <see langword="null"/> when this submission was a
    /// duplicate.
    /// </summary>
    /// <remarks>
    /// This is what makes the run's grounding checks free: it is carried in run state from ingest
    /// onwards so that Sprint 02's grounding gate queries it directly rather than re-opening and
    /// re-parsing the PDF once per field (design 5.1, 5.6).
    /// </remarks>
    public TextLayer? TextLayer { get; }

    /// <summary>Gets the content hash of the submitted bytes.</summary>
    public ContentHash ContentHash => Registered.ContentHash;
}
