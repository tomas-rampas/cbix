namespace Cbix.Core.Documents;

/// <summary>
/// One page of a document rendered to an image, ready to be presented to a vision-capable model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an image exists at all.</b> The permission matrix is a table, and a table is geometry:
/// which status code belongs to which product and client category is carried by the row and column
/// a cell sits in, not by the order the characters happen to come out of a text extractor. A model
/// that only ever sees the text layer is guessing at that geometry. This is the representation that
/// lets it see instead (design 5.1).
/// </para>
/// <para>
/// <b>The page number travels with the pixels.</b> Every extracted scalar is provenance-stamped
/// with the page it came from (design 5.4, 8), so an image whose page number had to be inferred
/// from its position in a list would make provenance depend on nobody reordering that list.
/// </para>
/// <para>
/// A class rather than a record, for the reason given on <see cref="Ingest.TextLayer"/>: records
/// advertise value equality, and this type's payload is a memory block whose compiler-generated
/// equality compares the underlying reference, so two byte-identical renders would report as
/// different. Equality has no use here - the identity that matters belongs to the document.
/// </para>
/// </remarks>
public sealed class PageImage
{
    /// <summary>Initialises a new <see cref="PageImage"/>.</summary>
    /// <param name="logicalPageNumber">
    /// The number of the page as a reader sees it in a PDF viewer, 1-based - the numbering the
    /// extraction prompts mandate and therefore the numbering an agent will report back in
    /// <c>ExtractedField.SourcePage</c>.
    /// </param>
    /// <param name="mediaType">IANA media type of <paramref name="content"/>, for example <c>image/png</c>.</param>
    /// <param name="content">
    /// The encoded image bytes. Held, not copied: the renderer produces this buffer and hands over
    /// ownership, and copying a multi-megabyte page render on every construction would be a real
    /// cost for a guarantee nothing here needs - a <see cref="ReadOnlyMemory{T}"/> is already
    /// read-only to every consumer this type has.
    /// <para>
    /// <b>Recorded decision: these bytes live in managed memory for the whole run scope, and are not
    /// scrubbed.</b> A prepared document is memoised for the lifetime of the workflow run and shared
    /// with the section-agent fan-out, so a rendered page's pixels sit on the managed heap - copied
    /// by the GC as it compacts, and eligible for a process dump or a page-file write - until the
    /// run ends. That is accepted rather than overlooked: the same document's bytes are already on
    /// local disk in the ingest share and its text is already resident in the
    /// <see cref="Ingest.TextLayer"/>, so pinning and zeroing image buffers would harden one copy of
    /// content that exists in several, at real cost to a hot path. It is written down because the
    /// calculation changes if this pipeline is ever pointed at documents whose content is
    /// restricted - today's corpus is synthetic specimens and client-facing regulatory manuals, not
    /// personal data.
    /// </para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="logicalPageNumber"/> is below <see cref="Ingest.TextLayer.FirstLogicalPageNumber"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="mediaType"/> is empty or white space, or <paramref name="content"/> is empty.</exception>
    public PageImage(int logicalPageNumber, string mediaType, ReadOnlyMemory<byte> content)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalPageNumber, Ingest.TextLayer.FirstLogicalPageNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        if (content.IsEmpty)
        {
            // A zero-byte image is not a page that happened to be blank - a blank page still
            // encodes to a valid white image. It is a render that failed and returned quietly, and
            // admitting one would present the model with a content block it cannot decode while
            // the run reports full visual fidelity.
            throw new ArgumentException(
                "A rendered page image must contain bytes; an empty buffer is a failed render, not a blank page.",
                nameof(content));
        }

        LogicalPageNumber = logicalPageNumber;
        MediaType = mediaType;
        Content = content;
    }

    /// <summary>Gets the 1-based logical page number this image renders.</summary>
    public int LogicalPageNumber { get; }

    /// <summary>Gets the IANA media type of <see cref="Content"/>.</summary>
    public string MediaType { get; }

    /// <summary>Gets the encoded image bytes.</summary>
    public ReadOnlyMemory<byte> Content { get; }
}
