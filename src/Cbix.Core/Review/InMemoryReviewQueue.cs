using System.Collections.Concurrent;

namespace Cbix.Core.Review;

/// <summary>
/// A process-local <see cref="IReviewQueue"/> holding rows in memory, in arrival order.
/// </summary>
/// <remarks>
/// <para>
/// The PoC and test sink; the SQL-backed one arrives with the persistence wiring in Sprint 03,
/// against the table sketched in <c>db/schema/review_queue.sql</c>. Following the house pattern the
/// in-memory registry and audit log set: <see cref="Entries"/> hands out a snapshot, so a reader
/// cannot reach in and edit the queue it is inspecting.
/// </para>
/// <para>
/// <b>It does not deduplicate, and that is honest rather than lazy.</b> The port says idempotency is
/// the implementation's problem, and this implementation cannot solve it usefully: it lives only as
/// long as the process, so the resumed-run replay it would be guarding against - which happens after
/// a restart - never reaches it. The SQL implementation is where the constraint belongs, and it has
/// one.
/// </para>
/// </remarks>
public sealed class InMemoryReviewQueue : IReviewQueue
{
    private readonly ConcurrentQueue<ReviewQueueEntry> _entries = new();

    /// <summary>Gets a point-in-time snapshot of the queue, oldest row first.</summary>
    public IReadOnlyList<ReviewQueueEntry> Entries => [.. _entries];

    /// <inheritdoc/>
    public Task EnqueueAsync(ReviewQueueEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        _entries.Enqueue(entry);

        return Task.CompletedTask;
    }
}
