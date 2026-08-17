using Cbix.Core.Documents;
using Cbix.Core.Extraction;
using Cbix.Core.Ingest;
using Cbix.Core.Review;
using Cbix.Core.Workflows;

using Microsoft.Extensions.Logging.Abstractions;

namespace Cbix.UnitTests.Workflows;

/// <summary>
/// The node a document reaches when the pipeline will not vouch for it (story S01-15).
/// </summary>
/// <remarks>
/// The workflow context is passed as <see langword="null"/> throughout: this executor does not use
/// it, and a test that manufactured one would be asserting against a runtime shape rather than
/// against the node. If the node ever starts using the context, these tests fail with a null
/// reference at the line that does - loudly, which is the point of not stubbing it.
/// </remarks>
public sealed class ReviewQueueStubExecutorTests
{
    private static readonly DateTimeOffset QueuedAt = new(2026, 8, 17, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_QueuesALowConfidenceDocumentWithTheConfidenceItReported()
    {
        InMemoryReviewQueue queue = new();

        ExtractionRunOutcome outcome = await Execute(queue, Profiled(0.15d));

        ReviewQueueEntry row = Assert.Single(queue.Entries);

        Assert.Equal(ReviewReason.TriageLowConfidence, row.Reason);
        Assert.Equal(0.15d, row.ReportedConfidence);
        Assert.Equal(CbixWorkflowNodes.Triage, row.AgentName);
        Assert.Equal(QueuedAt, row.QueuedUtc);

        // The confidence appears in the detail too, because a reviewer working a queue reads the text
        // before they read the columns - and "how close was it" is the first thing that decides whether
        // this is a new layout family or a bad scan.
        Assert.Contains("0.15", row.Detail, StringComparison.Ordinal);

        Assert.Equal(ExtractionRunDisposition.ReviewQueued, outcome.Disposition);
        Assert.Equal(0, outcome.SectionCount);
    }

    [Fact]
    public async Task HandleAsync_QueuesARefusedReplyUnderItsOwnReasonWithNoConfidence()
    {
        // The two arms want different responses from a human - a low confidence is a statement about
        // the document, a refused reply is a statement about the model, the prompt or the provider - so
        // merging them would send a reviewer to look at a PDF when the problem was a prompt.
        InMemoryReviewQueue queue = new();

        await Execute(queue, Refused("The triage agent's reply is not JSON."));

        ReviewQueueEntry row = Assert.Single(queue.Entries);

        Assert.Equal(ReviewReason.TriageReplyRefused, row.Reason);
        Assert.Null(row.ReportedConfidence);
        Assert.Contains("not JSON", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_BoundsTheDetailToWhatTheColumnHolds()
    {
        // The bound belongs where the text is built, not where it is stored: a limit enforced only at
        // the database is a limit discovered in Sprint 03, on a live queue, as a failed insert on the
        // document that most needed a human.
        InMemoryReviewQueue queue = new();

        await Execute(queue, Refused(new string('x', 4000)));

        ReviewQueueEntry row = Assert.Single(queue.Entries);

        Assert.True(row.Detail.Length <= ReviewQueueEntry.MaxDetailLength);
        Assert.EndsWith("...[truncated]", row.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_BoundsTheAgentNameToWhatTheColumnHolds()
    {
        // The sibling of the detail-bound test, and it guards the failure that made it necessary:
        // ReviewQueueEntry THROWS above its width, so an over-long agent name passed straight through
        // would fail the run on the document that most needed a human - the exact outcome the app-side
        // bound was added to prevent, arriving through the one field left unbounded at the call site.
        //
        // The assertion is therefore as much about what does NOT happen as about the truncation: the
        // call completes, a row lands, and the run carries on.
        InMemoryReviewQueue queue = new();

        ExtractionRunOutcome outcome = await Execute(
            queue,
            Profiled(0.2d, agentName: new string('a', 4000)));

        ReviewQueueEntry row = Assert.Single(queue.Entries);

        Assert.True(row.AgentName.Length <= ReviewQueueEntry.MaxAgentNameLength);
        Assert.EndsWith("...[truncated]", row.AgentName, StringComparison.Ordinal);
        Assert.Equal(ExtractionRunDisposition.ReviewQueued, outcome.Disposition);
    }

    [Fact]
    public async Task HandleAsync_RecordsTheDocumentsRegistryIdentity()
    {
        // The content hash, which is what every other table joins on and the one document identifier
        // that carries nothing an outside party chose. A row keyed on a file name would be a review
        // queue an uploader could address.
        InMemoryReviewQueue queue = new();

        await Execute(queue, Profiled(0.2d));

        Assert.Equal("sha256:" + new string('a', 64), Assert.Single(queue.Entries).DocumentId);
    }

    [Fact]
    public void Entry_BoundsEveryColumnItWillBeStoredIn()
    {
        // The same reasoning the detail bound already carries, applied to the other two text columns:
        // a bound enforced only at the database is a bound discovered in Sprint 03, on a live queue,
        // as a failed insert on the document that most needed a human. Refused rather than truncated,
        // because a shortened document id is a foreign key that matches nothing.
        Assert.Throws<ArgumentException>(() => new ReviewQueueEntry(
            new string('a', ReviewQueueEntry.MaxDocumentIdLength + 1),
            ReviewReason.TriageLowConfidence,
            "detail",
            "triage",
            0.2d,
            QueuedAt));

        Assert.Throws<ArgumentException>(() => new ReviewQueueEntry(
            "sha256:" + new string('a', 64),
            ReviewReason.TriageLowConfidence,
            "detail",
            new string('a', ReviewQueueEntry.MaxAgentNameLength + 1),
            0.2d,
            QueuedAt));

        Assert.Throws<ArgumentException>(() => new ReviewQueueEntry(
            "sha256:" + new string('a', 64),
            ReviewReason.TriageLowConfidence,
            new string('a', ReviewQueueEntry.MaxDetailLength + 1),
            "triage",
            0.2d,
            QueuedAt));
    }

    [Fact]
    public void Constructor_RejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ReviewQueueStubExecutor(
            null!,
            TimeProvider.System,
            NullLogger<ReviewQueueStubExecutor>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ReviewQueueStubExecutor(
            new InMemoryReviewQueue(),
            null!,
            NullLogger<ReviewQueueStubExecutor>.Instance));
        Assert.Throws<ArgumentNullException>(() => new ReviewQueueStubExecutor(
            new InMemoryReviewQueue(),
            TimeProvider.System,
            null!));
    }

    private static async Task<ExtractionRunOutcome> Execute(InMemoryReviewQueue queue, TriagedDocument message)
    {
        ReviewQueueStubExecutor executor = new(
            queue,
            new FixedTimeProvider(QueuedAt),
            NullLogger<ReviewQueueStubExecutor>.Instance);

        return await executor.HandleAsync(message, null!);
    }

    private static TriagedDocument Profiled(double confidence, string? agentName = null) =>
        new(
            Ingested(),
            agentName ?? CbixWorkflowNodes.Triage,
            "{}",
            new DocumentProfile("UNKNOWN", "UNKNOWN", "UNKNOWN", "UNKNOWN", "UNKNOWN", confidence),
            profileParseFailure: null);

    private static TriagedDocument Refused(string failure) =>
        new(Ingested(), CbixWorkflowNodes.Triage, "prose", profile: null, profileParseFailure: failure);

    private static DocumentIngestResult Ingested()
    {
        DocumentReference submitted = new(
            "sha256:" + new string('a', 64),
            new Uri(Path.Combine(Path.GetTempPath(), "cbix-review-queue", "specimen.pdf")),
            "specimen.pdf");

        return new DocumentIngestResult(
            submitted,
            new DocumentRegistryEntry(
                new ContentHash("sha256", new string('a', 64)),
                submitted,
                byteLength: 1024,
                firstSeenUtc: DateTimeOffset.UnixEpoch),
            isNewRegistration: true,
            new TextLayer(submitted.DocumentId, ["page one"]),
            contentHandle: null);
    }

    /// <summary>A clock that always reads the same instant, so a queued timestamp is assertable.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
