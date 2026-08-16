namespace Cbix.Core.Ingest;

/// <summary>
/// The append-only trail of what arrived at the ingest gate and what was decided about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a port and not <c>ILogger</c>.</b> "Duplicate submission → no-op, audit log entry"
/// (design 9) is a business outcome, not diagnostics. It has to be readable back: by the tests that
/// prove the no-op happened at all, by the review UI, and by whoever answers an audit question about
/// a document that was submitted three times. A log line is a string in a sink nobody can query
/// against a typed contract; this is state with a schema. Diagnostics still belong in
/// <c>ILogger</c> - the two are complementary, not alternatives.
/// </para>
/// <para>
/// <b>Append-only.</b> There is no update and no delete, here or in the store behind it, for the
/// same reason <c>field_provenance</c> and <c>extraction_runs</c> are append-only regardless of
/// deployment route (design 8): an audit trail that can be rewritten is not evidence.
/// </para>
/// </remarks>
public interface IIngestAuditLog
{
    /// <summary>Appends <paramref name="entry"/> to the audit trail.</summary>
    /// <param name="entry">The event to record.</param>
    /// <param name="cancellationToken">Token that cancels the append.</param>
    /// <returns>A task that completes when the entry is durably recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// <para>
    /// <b>Fail closed.</b> An implementation that cannot record must throw, and the caller must let
    /// that failure surface rather than swallowing it: an ingest step that proceeds after losing its
    /// audit record produces exactly the unexplainable published value the audit bar in design 8
    /// forbids. Implementations must be safe to call concurrently.
    /// </para>
    /// <para>
    /// Repeat submissions of the same document each produce their own entry - that repetition is the
    /// signal, so it is never collapsed.
    /// </para>
    /// </remarks>
    Task RecordAsync(IngestAuditEntry entry, CancellationToken cancellationToken = default);
}
