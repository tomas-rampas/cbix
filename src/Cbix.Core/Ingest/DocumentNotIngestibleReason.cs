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
}
