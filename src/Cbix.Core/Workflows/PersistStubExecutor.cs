using Cbix.Core.Diagnostics;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Cbix.Core.Workflows;

/// <summary>
/// The graph's terminal node for an extracted document: where a run ends and says so (design 5.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>It persists nothing, and the name says what it will be rather than what it is.</b> Staging
/// tables, the transactional publish, effective dating on <c>(doc_ref, doc_version)</c> and the
/// closing of superseded validity windows are Sprint 03. What this node delivers today is the
/// terminal state itself: it yields an <see cref="ExtractionRunOutcome"/> as the workflow's output,
/// so "the run completed" is an event a caller observes rather than an absence it infers from the
/// stream going quiet. S01-09's agnosticism proof asserts exactly that - "the run completes to the
/// persist step" - and a graph whose last node merely returned would give it nothing to assert on.
/// </para>
/// <para>
/// <b>It is one of two terminals, and the argument above is why the other one exists.</b> A
/// re-submission's run stops at the registry and never reaches here, so for that branch the reasoning
/// applied in reverse: its completion WAS an absence, indistinguishable from a stall.
/// <see cref="DuplicateTerminalExecutor"/> gives it the same treatment this node gets, which is what
/// makes "every run yields exactly one outcome, carrying its disposition" true of the graph rather
/// than of the happy path.
/// </para>
/// <para>
/// <b>Nothing reaches it unvalidated once Sprint 02 lands.</b> The validator becomes its only
/// upstream, and the validator is the sole authority to persist (the governing principle: agents
/// propose, code disposes). Wiring extraction straight into it here is a property of a topology with
/// no validator yet, not a shortcut that survives.
/// </para>
/// </remarks>
public sealed partial class PersistStubExecutor : Executor<SectionExtractionCandidate, ExtractionRunOutcome>
{
    private readonly ILogger<PersistStubExecutor> _logger;

    /// <summary>Initialises a new <see cref="PersistStubExecutor"/>.</summary>
    /// <param name="logger">Sink for this node's structured run events.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>No <see cref="ExecutorOptions"/> are supplied, and that is a measured constraint rather than
    /// a preference.</b> Suppressing the auto-<em>send</em> of this node's result would be tidier - it
    /// has no outgoing edge, so the sent message is produced only to be dropped - but on MAF 1.17.0
    /// <c>ExecutorOptions</c> has no accessible constructor (its only constructor is internal), is not
    /// a record so <c>with</c> is unavailable, and <c>ExecutorOptions.Default</c> is a single shared
    /// instance whose properties are ordinary settable ones. All three measured by reflection against
    /// the pinned package. Mutating <c>Default</c> to get the shape wanted here would silently change
    /// the behaviour of every executor in every workflow in the process, which is far worse than an
    /// unrouted message. So the default is taken: auto-yield gives the workflow its output, and the
    /// auto-sent message reaches no edge and is discarded.
    /// </para>
    /// <para>
    /// Revisit on upgrade. A public constructor or an <c>init</c>-shaped options type would make the
    /// suppression a one-line change, and design 11 says to expect this surface to move.
    /// </para>
    /// </remarks>
    public PersistStubExecutor(ILogger<PersistStubExecutor> logger)
        : base(CbixWorkflowNodes.Persist)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <summary>Ends the run and reports what reached it.</summary>
    /// <param name="message">The candidate that reached the persist step.</param>
    /// <param name="context">The workflow context for this superstep.</param>
    /// <param name="cancellationToken">Token that cancels the step.</param>
    /// <returns>The run's terminal outcome, yielded as the workflow's output.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public override ValueTask<ExtractionRunOutcome> HandleAsync(
        SectionExtractionCandidate message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        LogRunCompleted(
            _logger,
            message.Document.Document.Registered.DocumentId,
            message.SectionCount);

        return ValueTask.FromResult(
            new ExtractionRunOutcome(
                message.Document.Document.Submitted,
                ExtractionRunDisposition.Persisted,
                message.SectionCount));
    }

    /// <summary>Structured event marking a run that reached the persist step.</summary>
    [LoggerMessage(
        EventId = CbixEventIds.WorkflowRunCompleted,
        Level = LogLevel.Information,
        Message = "The run reached the persist step. Document={DocumentId}, sections={SectionCount}.")]
    private static partial void LogRunCompleted(ILogger logger, string documentId, int sectionCount);
}
