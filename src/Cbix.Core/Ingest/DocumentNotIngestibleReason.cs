namespace Cbix.Core.Ingest;

/// <summary>
/// Why the ingest gate refused a submission that was inside the ingest root but is not a document
/// the pipeline can process.
/// </summary>
/// <remarks>
/// Separate from <see cref="IngestRootViolationReason"/> because the operational response differs:
/// a containment refusal is a security event to alert on, while these are data-quality outcomes that
/// belong to whoever supplied the file.
/// </remarks>
public enum DocumentNotIngestibleReason
{
    /// <summary>
    /// The path names a directory rather than a file - most often the ingest root itself, submitted
    /// by a caller that meant to enumerate it.
    /// </summary>
    NotAFile = 0,

    /// <summary>
    /// The file contains no bytes. SHA-256 of nothing is a perfectly valid digest, which is exactly
    /// the problem: admitted, it would become one identity that every future empty submission
    /// collides with.
    /// </summary>
    Empty = 1,

    /// <summary>
    /// The file exceeds the configured maximum document size. Detected while reading, so a file that
    /// misreports its length or grows during the read is caught too.
    /// </summary>
    TooLarge = 2,

    /// <summary>
    /// The bytes are not a document the local PDF parser can read: not a PDF at all, truncated,
    /// structurally damaged, encrypted with a password the pipeline does not hold, or reporting no
    /// pages.
    /// </summary>
    /// <remarks>
    /// This belongs in the same family as the reasons above, and for the same operational reason:
    /// like an empty or oversized file, it is a data-quality outcome that belongs to whoever supplied
    /// the document, not a security event to alert on. It exists so that a parser failure surfaces as
    /// an ingest refusal with a reason an operator can act on, rather than as whatever exception the
    /// PDF library happened to raise - which for a plausibly-malformed file has been measured to be
    /// anything from the library's own format exception to a bare
    /// <see cref="ArgumentOutOfRangeException"/> about a negative offset.
    /// </remarks>
    Unreadable = 3,

    /// <summary>
    /// The document numbers its own pages - it carries a <c>/PageLabels</c> number tree - so the page
    /// number a reader sees need not be the physical page index the text layer is keyed by.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Unreadable"/> because the document is not damaged in any way: it
    /// parses perfectly, and refusing it is a statement about what this pipeline can currently be
    /// trusted to number correctly. It is the unknown-layout case of design 5.3 - route to review
    /// rather than guess - and an operator's response is to have a human confirm the numbering, not
    /// to ask the supplier for a better file.
    /// </remarks>
    UnsupportedPageNumbering = 4,
}
