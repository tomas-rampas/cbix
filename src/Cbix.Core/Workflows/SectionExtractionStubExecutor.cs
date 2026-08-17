using Microsoft.Agents.AI.Workflows;

namespace Cbix.Core.Workflows;

/// <summary>
/// The section-extraction slot, empty until story S01-16 puts the DocControl agent in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A stub with a real position in the graph.</b> It exists so the topology has the shape later
/// stories extend rather than build: a node downstream of triage, receiving the triaged document,
/// producing a candidate for the persist step. S01-16 replaces the body with the DocControl agent's
/// call; Sprint 02 replaces the node with the seven-way fan-out and a fan-in barrier. Neither has to
/// re-lay the spine.
/// </para>
/// <para>
/// <b>It calls no model and proposes no data.</b> That is what makes it honest as a stub: an
/// implementation that fabricated a plausible section would be indistinguishable, downstream, from an
/// extraction that had happened - and the governing principle is that nothing unvalidated may look
/// like a result. It passes the document through and reports zero sections.
/// </para>
/// </remarks>
public sealed class SectionExtractionStubExecutor : Executor<TriagedDocument, SectionExtractionCandidate>
{
    /// <summary>Initialises a new <see cref="SectionExtractionStubExecutor"/>.</summary>
    public SectionExtractionStubExecutor()
        : base(CbixWorkflowNodes.SectionExtraction)
    {
    }

    /// <summary>Passes the triaged document to the persist step with no sections extracted.</summary>
    /// <param name="message">The triaged document.</param>
    /// <param name="context">The workflow context for this superstep.</param>
    /// <param name="cancellationToken">Token that cancels the step.</param>
    /// <returns>A candidate carrying the document and no sections.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public override ValueTask<SectionExtractionCandidate> HandleAsync(
        TriagedDocument message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return ValueTask.FromResult(
            new SectionExtractionCandidate(message, new Dictionary<string, string>(StringComparer.Ordinal)));
    }
}
