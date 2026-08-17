namespace Cbix.Core.Review;

/// <summary>
/// Where a document goes when the pipeline will not vouch for it (design 5.7, design 6's
/// <c>review_queue</c> table).
/// </summary>
/// <remarks>
/// <para>
/// <b>A port, following the house pattern the registry and the audit log set (story S01-10):</b> a
/// neutral interface in <c>Cbix.Core</c>, an in-memory implementation for tests and for the offline
/// scenarios, and a T-SQL sketch in <c>db/schema/</c> so the eventual SQL-backed implementation and
/// the in-memory one cannot drift apart unnoticed. Sprint 03 supplies the SQL one; the lifetime does
/// not change when it does.
/// </para>
/// <para>
/// <b>Enqueuing is all this port does, and that is Sprint 01's whole scope for it.</b> Claiming a
/// row, resolving it, and resuming the paused run from its checkpoint are Sprint 03's HITL work over
/// MAF's request/response ports. Declaring those methods now would be an interface written against a
/// mechanism nobody has built - and the first implementation of them would have to be a lie or a
/// throw.
/// </para>
/// <para>
/// <b>Process-wide, not run-scoped.</b> A queue that only existed for the duration of the run that
/// filled it would lose every row the moment the run ended, which is the one thing a review queue
/// must not do.
/// </para>
/// </remarks>
public interface IReviewQueue
{
    /// <summary>Records that a document needs a human.</summary>
    /// <param name="entry">The row to write.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>A task that completes when the row is durable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <b>Idempotency is the implementation's problem, not the caller's.</b> A resumed run replays the
    /// superstep that queued the document, so the same row will be offered more than once for reasons
    /// that are nobody's bug. An implementation that produced a duplicate row per resume would grow the
    /// queue every time a worker restarted - so the SQL-backed one keys on the document and the reason
    /// rather than inserting blindly.
    /// </remarks>
    Task EnqueueAsync(ReviewQueueEntry entry, CancellationToken cancellationToken = default);
}
