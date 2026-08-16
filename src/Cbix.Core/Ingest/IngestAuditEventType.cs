namespace Cbix.Core.Ingest;

/// <summary>
/// What happened to a document at the ingest gate, as recorded in the ingest audit log.
/// </summary>
public enum IngestAuditEventType
{
    /// <summary>
    /// The submitted bytes had not been seen before and a registry record was created for them.
    /// </summary>
    DocumentRegistered = 0,

    /// <summary>
    /// The submitted bytes were already registered, so no registry record was created and the
    /// submission proceeded no further (design 9, "Duplicate submission": registry hash match,
    /// handled as a no-op plus an audit log entry).
    /// </summary>
    DuplicateSubmissionIgnored = 1,
}
