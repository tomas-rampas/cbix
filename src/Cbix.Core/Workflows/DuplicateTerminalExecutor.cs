using Cbix.Core.Ingest;

using Microsoft.Agents.AI.Workflows;

namespace Cbix.Core.Workflows;

/// <summary>
/// The graph's second terminal: where a re-submission's run ends, saying so (design 5.1, design 9
/// "Duplicate submission").
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists so that every run yields exactly one output.</b> The duplicate branch previously just
/// stopped - the conditional edge out of ingest declined to fire and nothing else ran - which left a
/// caller inferring "duplicate" from the absence of a <c>WorkflowOutputEvent</c>. That is precisely
/// the signal a crashed or stalled run gives, so the two were indistinguishable to anything watching
/// the event stream. A node that costs one edge and one allocation removes the ambiguity for good.
/// </para>
/// <para>
/// <b>It logs nothing, deliberately.</b> The decision was made and recorded upstream: ingest emits the
/// structured "run stopped at the registry" event at the point it read the registry, and the ingest
/// audit log carries the <c>DuplicateSubmissionIgnored</c> entry. A second event here would
/// double-count duplicates for anyone aggregating on either signal. What this node adds is the typed
/// terminal value, not another line in the log.
/// </para>
/// <para>
/// <b>Not a failure path.</b> A duplicate is the pipeline behaving as designed, so it ends in an
/// ordinary outcome rather than an exception - which is also what keeps it out of the retry and
/// review edges Sprint 02 hangs off failures.
/// </para>
/// </remarks>
public sealed class DuplicateTerminalExecutor : Executor<DocumentIngestResult, ExtractionRunOutcome>
{
    /// <summary>Initialises a new <see cref="DuplicateTerminalExecutor"/>.</summary>
    public DuplicateTerminalExecutor()
        : base(CbixWorkflowNodes.DuplicateTerminal)
    {
    }

    /// <summary>Ends the run, reporting that the document was already registered.</summary>
    /// <param name="message">The ingest outcome for a document that was already known.</param>
    /// <param name="context">The workflow context for this superstep.</param>
    /// <param name="cancellationToken">Token that cancels the step.</param>
    /// <returns>The run's terminal outcome, yielded as the workflow's output.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The message is a new registration. The edge into this node filters on exactly that, so reaching
    /// here with one means the graph is mis-wired - and the silent version of that bug is a document
    /// being reported as a duplicate and never extracted.
    /// </exception>
    public override ValueTask<ExtractionRunOutcome> HandleAsync(
        DocumentIngestResult message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.IsNewRegistration)
        {
            throw new ArgumentException(
                "A newly registered document reached the duplicate terminal, so it would be reported as "
                    + "already processed and never extracted. The edge out of ingest is mis-wired.",
                nameof(message));
        }

        return ValueTask.FromResult(
            new ExtractionRunOutcome(
                message.Submitted,
                ExtractionRunDisposition.DuplicateIgnored,
                sectionCount: 0));
    }
}
