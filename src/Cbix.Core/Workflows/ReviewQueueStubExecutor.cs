using System.Globalization;

using Cbix.Core.Diagnostics;
using Cbix.Core.Review;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Cbix.Core.Workflows;

/// <summary>
/// Where a document the pipeline will not vouch for ends its run: a row in the review queue, and a
/// terminal outcome saying so (design 5.7, design 9 "New/unknown layout family").
/// </summary>
/// <remarks>
/// <para>
/// <b>A stub in one respect only, and the respect matters less than it sounds.</b> The row it writes
/// is real, the port it writes through is the one Sprint 03's SQL-backed queue implements, and the
/// routing edge that brings a document here is the production edge. What is absent is the pause and
/// resume: Sprint 03 replaces this terminal with a checkpointed wait on MAF's request/response ports,
/// from which an approved or corrected document continues to persist. Until then a routed run ends
/// here, which is the honest shape - a run that pretended to wait for a reviewer nobody could answer
/// would be worse than one that stops and says where the document went.
/// </para>
/// <para>
/// <b>It yields an outcome because every terminal branch must.</b> S01-13's invariant is that a run
/// emits exactly one <c>WorkflowOutputEvent</c> carrying its disposition; a review branch that simply
/// stopped would be indistinguishable from a stalled run to anything watching the stream, which is
/// the ambiguity the duplicate terminal was built to remove and this must not reintroduce.
/// </para>
/// <para>
/// <b>Nothing the model wrote reaches a log line from here.</b> The row carries the detail - stored,
/// where it can be rendered deliberately by a review UI - and the log carries the document identity,
/// the reason and the confidence, all of which are either derived from the bytes or written by CBIX.
/// </para>
/// </remarks>
public sealed partial class ReviewQueueStubExecutor : Executor<TriagedDocument, ExtractionRunOutcome>
{
    private readonly IReviewQueue _reviewQueue;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReviewQueueStubExecutor> _logger;

    /// <summary>Initialises a new <see cref="ReviewQueueStubExecutor"/>.</summary>
    /// <param name="reviewQueue">The queue this node writes to.</param>
    /// <param name="timeProvider">The clock, so the queued timestamp is injected rather than ambient.</param>
    /// <param name="logger">Sink for this node's structured run events.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ReviewQueueStubExecutor(
        IReviewQueue reviewQueue,
        TimeProvider timeProvider,
        ILogger<ReviewQueueStubExecutor> logger)
        : base(CbixWorkflowNodes.Review)
    {
        ArgumentNullException.ThrowIfNull(reviewQueue);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _reviewQueue = reviewQueue;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Queues one document for a human and ends its run.</summary>
    /// <param name="message">The triaged document the routing edge sent here.</param>
    /// <param name="context">The workflow context for this superstep.</param>
    /// <param name="cancellationToken">Token that cancels the write.</param>
    /// <returns>The run's terminal outcome, yielded as the workflow's output.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public override async ValueTask<ExtractionRunOutcome> HandleAsync(
        TriagedDocument message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        (ReviewReason reason, string detail) = Describe(message);

        ReviewQueueEntry entry = new(
            message.Document.Registered.DocumentId,
            reason,
            detail,

            // Truncated here, exactly as the detail is, and for the reason the entry's own bound
            // exists: ReviewQueueEntry THROWS above its width, so passing an over-long agent name
            // straight through would fail the run on the document that most needed a human - the very
            // outcome the app-side bound was added to prevent, arriving through the one field that was
            // left unbounded at the call site. An agent name is a graph node id today, so this can only
            // fire if a host registers an absurd one; that is precisely when it must not take the
            // document down with it.
            Truncate(message.AgentName, ReviewQueueEntry.MaxAgentNameLength),
            message.Profile?.Confidence,
            _timeProvider.GetUtcNow());

        await _reviewQueue.EnqueueAsync(entry, cancellationToken).ConfigureAwait(false);

        LogRunRoutedToReview(
            _logger,
            entry.DocumentId,
            entry.Reason,
            entry.ReportedConfidence ?? double.NaN);

        return new ExtractionRunOutcome(
            message.Document.Submitted,
            ExtractionRunDisposition.ReviewQueued,
            sectionCount: 0);
    }

    /// <summary>Turns a triaged document into the reason and description a reviewer is handed.</summary>
    /// <remarks>
    /// The two arms are the two ways of not knowing, kept apart because they want different responses:
    /// a low confidence is a statement about the document (add few-shots for the family, per design 9),
    /// while a refused reply is a statement about the model, the prompt or the provider. Merging them
    /// would send a reviewer to look at a PDF when the problem was a prompt.
    /// </remarks>
    private static (ReviewReason Reason, string Detail) Describe(TriagedDocument message)
    {
        if (message.Profile is { } profile)
        {
            return (
                ReviewReason.TriageLowConfidence,
                Truncate(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Triage reported confidence {profile.Confidence} for this document, below the "
                        + $"configured routing threshold, so it was not extracted. Confirm what the "
                        + $"document is and, if the layout is a new family, add few-shot examples for "
                        + $"it."), ReviewQueueEntry.MaxDetailLength));
        }

        // Written by the parser, which sanitises anything model-supplied before it appears - see
        // DocumentProfileParser. Truncated here rather than there because the bound belongs to the
        // column this row is destined for.
        return (
            ReviewReason.TriageReplyRefused,
            Truncate(
                "Triage's agent replied with something that is not a DocumentProfile, so nothing about "
                    + "this document was established. "
                    + (message.ProfileParseFailure ?? "No reason was recorded."),
                ReviewQueueEntry.MaxDetailLength));
    }

    /// <summary>Bounds a value to the width of the column it is destined for.</summary>
    /// <remarks>
    /// Deliberate and visible: the marker is appended after truncation so a reviewer can tell a
    /// shortened description from a terse one, and so nothing in the truncated text can forge the
    /// marker.
    /// </remarks>
    private static string Truncate(string value, int maxLength)
    {
        const string Marker = "...[truncated]";

        return value.Length <= maxLength
            ? value
            : value[..(maxLength - Marker.Length)] + Marker;
    }

    /// <summary>Structured event for a run that ended in the review queue.</summary>
    /// <remarks>
    /// <para>
    /// Information, not Warning: design 9 lists an unrecognised document as a handled case, and this is
    /// the pipeline doing the thing it was built to do rather than failing to. What is worth an alert
    /// is the <em>rate</em>, which an operator computes from these.
    /// </para>
    /// <para>
    /// The confidence is rendered as NaN when there was no parseable profile. That is not a missing
    /// value dressed up as a number - it is the one double that cannot be mistaken for a real
    /// confidence, and the reason on the same line already says which case this is.
    /// </para>
    /// </remarks>
    [LoggerMessage(
        EventId = CbixEventIds.WorkflowRunRoutedToReview,
        Level = LogLevel.Information,
        Message = "The run was routed to the review queue instead of into extraction. "
            + "Document={DocumentId}, reason={Reason}, reportedConfidence={ReportedConfidence}.")]
    private static partial void LogRunRoutedToReview(
        ILogger logger,
        string documentId,
        ReviewReason reason,
        double reportedConfidence);
}
