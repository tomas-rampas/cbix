using Cbix.Core.Ingest;

using Microsoft.Agents.AI.Workflows;

namespace Cbix.Core.Workflows;

/// <summary>
/// Builds the CBIX workflow graph (design 5.2). This is the workflow half of the composition root,
/// and it lives in <c>Cbix.Core</c> so the graph can be built and run without the executable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why here and not in the host.</b> CLAUDE.md splits the composition root: workflow-graph
/// composition in Core, provider selection and credential wiring in
/// <c>Cbix.Worker.CbixWorkerHostExtensions.AddCbixWorker</c>. The reason is testability with teeth -
/// S01-09's agnosticism proof has to run <em>this graph</em>, not a copy of it, against a stub chat
/// client, and a graph assembled inside the worker executable could only be exercised by starting the
/// worker. The split also falls out of the containment rules: naming a provider adapter is exactly
/// what Core may not do, and choosing one is exactly what a host must.
/// </para>
/// <para>
/// <b>The topology, and where each later story attaches.</b>
/// </para>
/// <code>
/// ingest --[IsNewRegistration]------&gt; triage --[vouched for]---&gt; sectionExtraction --&gt; persist
///        \--[already registered]----&gt; duplicateTerminal    \--[needs review]------&gt; review
/// </code>
/// <para>
/// <b>All three branches end in a node that yields an <see cref="ExtractionRunOutcome"/>,</b> so
/// every run emits exactly one <c>WorkflowOutputEvent</c> carrying its
/// <see cref="ExtractionRunDisposition"/>. The duplicate branch used to have no second edge at all -
/// ingest's message simply matched nothing and the run went quiet - which made "this was a duplicate"
/// and "this run stalled or crashed" the same observation to anything watching the stream. Two
/// conditional edges over the same predicate, one node each, is what makes the terminal state total,
/// and the review branch added by S01-15 is held to the same standard.
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>S01-16</b> replaces <see cref="SectionExtractionStubExecutor"/> with the real DocControl
///   agent in the same slot.
///   </description></item>
///   <item><description>
///   <b>Sprint 02</b> widens <c>sectionExtraction</c> into the seven-way fan-out plus a fan-in
///   barrier, and inserts the normaliser and the validator before <c>persist</c>.
///   </description></item>
///   <item><description>
///   <b>Sprint 03</b> adds checkpointing. Deliberately absent here: MAF checkpoints per superstep and
///   the storage provider is the story that needs SQL Server, so building it now would be a
///   half-configured checkpoint store nothing resumes from.
///   </description></item>
/// </list>
/// <para>
/// <b>Cache-priming stagger (recorded, story S01-05's review).</b> An Anthropic prompt-cache entry
/// becomes readable only once the response that writes it has begun, so a simultaneous seven-way
/// fan-out over an uncached document all misses and every branch pays the write premium instead of
/// collecting the discount. In <em>this</em> topology the requirement is satisfied trivially and by
/// construction: <c>triage</c> is the only model call and there is a single linear edge out of it, so
/// exactly one call primes the cache and everything downstream follows it. The obligation transfers
/// intact to Sprint 02: the section fan-out must be a successor of <c>triage</c>, never a peer of it.
/// A fan-out edge added <em>from</em> <c>ingest</c> alongside <c>triage</c> would be the mistake, and
/// it would be invisible - the run would be correct and the bill would be wrong.
/// </para>
/// <para>
/// <b>One factory instance per run.</b> The executors it is given are run-scoped, because the ingest
/// service and the document-content profile they hold are; see <c>AddCbixWorkflow</c>. Building the
/// <see cref="Workflow"/> is cheap and the object is immutable, so a per-run build costs nothing
/// worth caching and removes any question about executors being shared across runs.
/// </para>
/// </remarks>
public sealed class CbixWorkflowFactory
{
    private readonly DocumentIngestExecutor _ingest;
    private readonly TriageExecutor _triage;
    private readonly SectionExtractionStubExecutor _sectionExtraction;
    private readonly PersistStubExecutor _persist;
    private readonly DuplicateTerminalExecutor _duplicateTerminal;
    private readonly ReviewQueueStubExecutor _review;
    private readonly TriageRoutingOptions _routing;

    /// <summary>Initialises a new <see cref="CbixWorkflowFactory"/>.</summary>
    /// <param name="ingest">The ingest node.</param>
    /// <param name="triage">The triage node.</param>
    /// <param name="sectionExtraction">The section-extraction node.</param>
    /// <param name="persist">The terminal node for an extracted document.</param>
    /// <param name="duplicateTerminal">The terminal node for a re-submission.</param>
    /// <param name="review">The terminal node for a document the pipeline will not vouch for.</param>
    /// <param name="routing">Where triage's routing edge sits.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public CbixWorkflowFactory(
        DocumentIngestExecutor ingest,
        TriageExecutor triage,
        SectionExtractionStubExecutor sectionExtraction,
        PersistStubExecutor persist,
        DuplicateTerminalExecutor duplicateTerminal,
        ReviewQueueStubExecutor review,
        TriageRoutingOptions routing)
    {
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(sectionExtraction);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(duplicateTerminal);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(routing);

        _ingest = ingest;
        _triage = triage;
        _sectionExtraction = sectionExtraction;
        _persist = persist;
        _duplicateTerminal = duplicateTerminal;
        _review = review;
        _routing = routing;
    }

    /// <summary>Builds the graph for one run.</summary>
    /// <returns>The workflow, ready to hand to a MAF execution environment.</returns>
    public Workflow Build()
    {
        // Read once, into a local the two closures below capture. Reading the option inside each
        // predicate would be equivalent today and would silently stop being so the moment the options
        // object became mutable or reloadable - at which point one edge could see a new threshold and
        // the other an old one, and a document would match both or neither.
        double threshold = _routing.ReviewConfidenceThreshold;

        return new WorkflowBuilder(_ingest)
            .WithName("cbix-extraction")
            .WithDescription("Ingest, triage, section extraction and persist for one country instruction document.")

            // Conditional, and this is the design's "a duplicate's run stops at the registry" (5.1)
            // expressed as graph shape rather than as a check inside a node. A duplicate carries no
            // text layer and no content handle, so letting one through would put a model in front of a
            // document nobody prepared - and it would pay for the privilege.
            //
            // The null check is not defensive noise. MAF types the condition as Func<T?, bool>,
            // because a condition is also consulted for messages that are not of T at all - so a
            // predicate written as `result.IsNewRegistration` would throw inside the routing layer
            // the first time any other message type crossed this edge. `is true` refuses null, which
            // is the right answer here: an absent ingest result is not a new registration.
            .AddEdge<DocumentIngestResult>(_ingest, _triage, result => result?.IsNewRegistration is true)

            // The other half of the same predicate, and the reason ingest has exactly two edges. Written
            // as `is false` rather than `is not true` so that null - a message of some other type, which
            // MAF also consults this condition for - matches NEITHER edge. Routing an unknown message to
            // the duplicate terminal would report a document as already processed on the strength of a
            // type mismatch.
            .AddEdge<DocumentIngestResult>(_ingest, _duplicateTerminal, result => result?.IsNewRegistration is false)

            // Design 9's "New/unknown layout family" row, expressed as graph shape (story S01-15). The
            // same two-conditional-edges-over-one-predicate arrangement the ingest split uses, and for
            // the same reason: the decision is visible in the topology rather than buried in a node,
            // and every future node hung off triage inherits it.
            //
            // `is true` / `is false` rather than a negation, again deliberately. MAF types a condition
            // as Func<T?, bool> because it consults the condition for messages that are not of T at all,
            // so a null must match NEITHER edge - a message of some other type routed to review would
            // queue a document for a human on the strength of a type mismatch, and routed onward would
            // extract one on the same basis.
            .AddEdge<TriagedDocument>(_triage, _review, triaged => triaged?.RequiresReview(threshold) is true)
            .AddEdge<TriagedDocument>(_triage, _sectionExtraction, triaged => triaged?.RequiresReview(threshold) is false)

            .AddEdge(_sectionExtraction, _persist)

            // The terminal designation, and ALL THREE terminals carry it. Without it the outcome a
            // terminal node yields never surfaces as a WorkflowOutputEvent, and "the run completed" would
            // be inferred from the event stream ending - which is also what a stall looks like. Naming
            // every terminal is what makes that inference unnecessary on each branch rather than only on
            // the happy one, and the review branch is the one where it matters most: a document queued
            // for a human that also looked like a crashed run would be found twice or not at all.
            .WithOutputFrom(_persist, _duplicateTerminal, _review)
            .Build();
    }
}
