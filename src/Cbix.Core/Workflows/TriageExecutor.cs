using Cbix.Core.Diagnostics;
using Cbix.Core.Ingest;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Cbix.Core.Workflows;

/// <summary>
/// The triage agent's slot in the graph: the first - and, in this topology, the only - model call
/// before extraction (design 5.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The agent is injected, and the executor never learns which provider is behind it.</b> It takes
/// an <see cref="AIAgent"/>: MAF's neutral currency. A canned <c>IChatClient</c> wrapped in a
/// <c>ChatClientAgent</c> satisfies it exactly as well as the Anthropic adapter does, which is what
/// lets S01-09 run this whole graph with no provider package on the path, and what makes swapping
/// provider a change to one registration in the host.
/// </para>
/// <para>
/// <b>What story S01-14 changes, and what it does not.</b> It replaces the prompt below with the real
/// triage instructions and structured-output schema, and parses the answer into design Appendix A's
/// <c>DocumentProfile</c>. The node, its id, its position in the graph and its message types are
/// unaffected - which is the point of landing the slot before the agent that fills it.
/// </para>
/// <para>
/// <b>What story S01-15 changes.</b> Nothing here. Its low-confidence and unknown-document routing is
/// a pair of conditional edges out of this node, added in
/// <see cref="CbixWorkflowFactory"/>; a node that decided its own routing would put the graph's shape
/// in two places at once.
/// </para>
/// <para>
/// <b>Cache priming (recorded, story S01-05's review).</b> An Anthropic prompt-cache entry only
/// becomes readable once the response that writes it has begun, so a simultaneous fan-out over an
/// uncached document all misses and every branch pays the write premium. This node is the primer: it
/// is the sole model call in the graph and runs strictly before anything downstream, so in this
/// topology the stagger holds by construction rather than by scheduling. Sprint 02's seven-way
/// section fan-out must preserve that ordering - triage strictly before the fan-out, never alongside
/// it - and the cost of losing it is measured money, not correctness, so nothing will fail to report
/// it.
/// </para>
/// </remarks>
public sealed partial class TriageExecutor : Executor<DocumentIngestResult, TriagedDocument>
{
    /// <summary>
    /// The prompt this slot sends until story S01-14 supplies the real one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It follows the uniform extraction prompting rules (CLAUDE.md) even though its answer is not yet
    /// parsed, because a placeholder that broke them would be the version somebody copied.
    /// </para>
    /// <para>
    /// <b>NO DOCUMENT IS ATTACHED TO THIS CALL, and the prompt currently lies about that.</b> It says
    /// "the supplied document" while <see cref="HandleAsync"/> runs a bare
    /// <see cref="AIAgent"/> - so today's model is asked to identify a document it is never shown, and
    /// answers anyway. That is harmless only because nothing parses the answer yet. <b>Attaching the
    /// document is story S01-14's obligation</b>, and it is not optional garnish: measured in S01-05,
    /// MAF's Anthropic integration silently drops an <c>AIContent</c> whose only payload is
    /// <c>RawRepresentation</c>, so content must be attached through the provider's own route -
    /// <c>ChatOptions.RawRepresentationFactory</c>, which is what
    /// <see cref="Cbix.Core.Agents.BoundDocumentAgent"/> exists to encapsulate. S01-14 therefore
    /// changes this slot's agent dependency to a document-bound one and runs it against
    /// <see cref="DocumentIngestResult.ContentHandle"/>. A green triage with no document block on the
    /// wire is a hallucination that looks like an extraction.
    /// </para>
    /// </remarks>
    private const string TriagePrompt =
        "Identify this document. Use only the supplied document; extract, never interpret; return "
            + "nothing you cannot read in it.";

    private readonly AIAgent _agent;
    private readonly ILogger<TriageExecutor> _logger;

    /// <summary>Initialises a new <see cref="TriageExecutor"/>.</summary>
    /// <param name="agent">
    /// The triage agent. Supplied by the host, which is where the provider is chosen; Core requires
    /// only that it is an <see cref="AIAgent"/>.
    /// </param>
    /// <param name="logger">Sink for this node's structured run events.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public TriageExecutor(AIAgent agent, ILogger<TriageExecutor> logger)
        : base(CbixWorkflowNodes.Triage)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(logger);

        _agent = agent;
        _logger = logger;
    }

    /// <summary>Runs triage over one ingested document.</summary>
    /// <param name="message">The ingest outcome. Always a new registration - see the remarks.</param>
    /// <param name="context">The workflow context for this superstep.</param>
    /// <param name="cancellationToken">Token that cancels the model call.</param>
    /// <returns>The document paired with what the triage agent said about it.</returns>
    /// <remarks>
    /// The edge into this node already filters duplicates, and <see cref="TriagedDocument"/> refuses
    /// one outright. Both, deliberately: the edge is the design (design 5.1 - a duplicate's run stops
    /// at the registry) and the constructor is the backstop, because the failure it prevents is a
    /// model answering confidently about a document nobody prepared, which no downstream assertion
    /// would catch.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public override async ValueTask<TriagedDocument> HandleAsync(
        DocumentIngestResult message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        LogTriageStarting(_logger, message.Registered.DocumentId, _agent.Name ?? CbixWorkflowNodes.Triage);

        AgentResponse response = await _agent
            .RunAsync(TriagePrompt, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return new TriagedDocument(
            message,
            _agent.Name ?? CbixWorkflowNodes.Triage,
            response.Text);
    }

    /// <summary>Structured event marking the run's first model call.</summary>
    /// <remarks>
    /// Emitted before the call rather than after it, because this is the call that primes the prompt
    /// cache: an operator reading a cost anomaly needs the point at which the priming call started,
    /// and a call that never returns logs nothing at all if the event waits for a response. The agent
    /// name is part of design 8's provenance record.
    /// </remarks>
    [LoggerMessage(
        EventId = CbixEventIds.WorkflowTriageStarting,
        Level = LogLevel.Information,
        Message = "Triage is calling the model. Document={DocumentId}, agent={AgentName}.")]
    private static partial void LogTriageStarting(ILogger logger, string documentId, string agentName);
}
