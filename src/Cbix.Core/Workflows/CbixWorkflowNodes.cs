namespace Cbix.Core.Workflows;

/// <summary>
/// The identifiers of the nodes in the CBIX workflow graph.
/// </summary>
/// <remarks>
/// <para>
/// Executor ids are what MAF reports on every <c>ExecutorInvokedEvent</c>, <c>ExecutorCompletedEvent</c>
/// and <c>ExecutorFailedEvent</c>, so they are the names an operator reads in a trace and the names a
/// test asserts an ordering on. Spelling them once here is what keeps those two from drifting apart -
/// a renamed node with a test that still asserts the old string passes vacuously, because an assertion
/// that no executor with that id ran is trivially satisfiable.
/// </para>
/// <para>
/// The names are the design's own (5.2, and the diagram in 4), not abbreviations invented here.
/// </para>
/// </remarks>
public static class CbixWorkflowNodes
{
    /// <summary>The ingest executor: hash, dedupe, registry, text layer, document preparation (design 5.1).</summary>
    public const string Ingest = "ingest";

    /// <summary>The triage agent's executor slot (design 5.3).</summary>
    public const string Triage = "triage";

    /// <summary>
    /// The section-extraction slot. One node in this sprint; Sprint 02 widens it to the seven-way
    /// fan-out and a fan-in barrier.
    /// </summary>
    public const string SectionExtraction = "sectionExtraction";

    /// <summary>The persist step - the graph's terminal node for an extracted document (design 5.8).</summary>
    public const string Persist = "persist";

    /// <summary>
    /// The review-queue node: where a document the pipeline will not vouch for ends its run
    /// (design 5.7, design 9 "New/unknown layout family").
    /// </summary>
    /// <remarks>
    /// A terminal in Sprint 01 and deliberately not one afterwards. Sprint 03's HITL work turns it into
    /// a checkpointed pause over MAF's request/response ports, from which an approved or corrected
    /// document continues to persist - so the node id stays and what hangs off it changes.
    /// </remarks>
    public const string Review = "review";

    /// <summary>
    /// The terminal node for a re-submission, whose run stops at the registry (design 5.1).
    /// </summary>
    /// <remarks>
    /// A second terminal rather than a branch inside <see cref="Persist"/>: the two ends mean
    /// genuinely different things, and an operator reading a trace should see which one a run took
    /// without decoding a payload.
    /// </remarks>
    public const string DuplicateTerminal = "duplicateTerminal";
}
