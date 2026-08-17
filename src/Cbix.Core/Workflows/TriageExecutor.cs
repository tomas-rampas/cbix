using Cbix.Core.Agents;
using Cbix.Core.Diagnostics;
using Cbix.Core.Documents;
using Cbix.Core.Extraction;
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
/// an <see cref="IDocumentBoundAgentFactory"/>: Core's neutral seam for "give this node an agent that
/// can actually see the run's document". A canned <c>IChatClient</c> wrapped in a
/// <c>ChatClientAgent</c> and adapted by <see cref="ChatContentDocumentAgentFactory"/> satisfies it
/// exactly as well as the Anthropic adapter's own factory does, which is what lets S01-09 run this
/// whole graph with no provider package on the path.
/// </para>
/// <para>
/// <b>The document is attached, and the split between the two ways of attaching it lives below this
/// node.</b> Ingest prepares the document once and carries a handle; this node redeems that handle
/// against the same run-scoped <see cref="IDocumentContentProvider"/> - memoised, so redeeming costs
/// nothing on the Claude path and a local re-parse on the others - and hands the prepared content to
/// the factory. What the factory does with it depends on the profile that produced it: the Claude
/// native-PDF profile's blocks travel through the provider's own request shape, because MAF's
/// Anthropic integration was measured silently dropping an <c>AIContent</c> whose only payload is
/// <c>RawRepresentation</c>; the text-only and generic-vision profiles' blocks - page markers,
/// verbatim page text, rendered images - travel as ordinary chat content, which is how the text layer
/// reaches the model when no native document mode is available. This node asks for content and never
/// asks which profile produced it, which is what <see cref="IDocumentContentProvider"/> requires of
/// every caller.
/// </para>
/// <para>
/// <b>What the model says is a proposal; what this node returns is a decision.</b> The reply is
/// parsed strictly against design Appendix A's <c>DocumentProfile</c> contract by
/// <see cref="DocumentProfileParser"/>. A reply that does not match it produces a
/// <see cref="TriagedDocument"/> carrying the refusal rather than a profile, and the routing edges
/// (story S01-15) send that document to review exactly as they send a low-confidence one. Refusing
/// here rather than throwing is deliberate: design 9 lists an unrecognised document as a handled
/// case, not a failure, and a routine outcome on the exception path teaches operators to ignore
/// exceptions.
/// </para>
/// <para>
/// <b>Routing is decided by the graph, not here.</b> This node computes the profile; the conditional
/// edges out of it compare the confidence against
/// <see cref="TriageRoutingOptions.ReviewConfidenceThreshold"/>. A node that decided its own routing
/// would put the graph's shape in two places at once.
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
    /// The turn triage sends, alongside the prepared document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It states the contract the parser will enforce, and it is built from the parser's own field
    /// list</b> so the two cannot drift: a prompt that asked for a field the parser rejects would
    /// route every document to review, and one that omitted a field the parser requires would do the
    /// same, both for a reason nobody would find in the model's reply.
    /// </para>
    /// <para>
    /// <b>It obeys the uniform extraction prompting rules (CLAUDE.md), with one reconciliation stated
    /// rather than fudged.</b> Those rules say to return null for an absent field and never invent
    /// one. Design Appendix A declares every <c>DocumentProfile</c> member as a value, because triage
    /// output is routing metadata rather than published data - so "I cannot read this" is expressed
    /// the way design 5.3 says it should be, as the literal <c>UNKNOWN</c> plus a low confidence,
    /// which routes the whole document to a human instead of into extraction. That is the same
    /// refusal the rule protects, taking the channel the design already built for it.
    /// </para>
    /// <para>
    /// The system instructions - the uniform rules themselves - are the host's, supplied when it
    /// constructs the agent, because constructing one means choosing a provider and a model tier.
    /// What is here is triage's own question, which is provider-independent and therefore Core's.
    /// </para>
    /// </remarks>
    private static readonly string TriagePrompt =
        "Identify the supplied document and reply with a single JSON object carrying exactly these "
            + $"fields and no others: {string.Join(", ", DocumentProfileParser.ContractFields)}.\n"
            + "DocType: what kind of document this is, as the document itself names it.\n"
            + "JurisdictionIso: the ISO 3166-1 alpha-2 code of the country whose rules the document "
            + "states.\n"
            + "DocRef: the document's own reference identifier.\n"
            + "Version: the document's own version designation.\n"
            + "LayoutFamily: a short, lower-case, hyphenated name for this document's layout, stable "
            + "across issues of the same template.\n"
            + "Confidence: a number between 0 and 1 for how certain the fields above are.\n"
            + "Use only the supplied document. Extract, never interpret; copy values verbatim. Where "
            + "the document does not state a value, or you cannot read it, give that field the exact "
            + "string UNKNOWN and lower Confidence accordingly - never invent one, and never guess at "
            + "a document you do not recognise.\n"
            + "Reply with the JSON object alone: no prose, no explanation, no code fence.";

    private readonly IDocumentBoundAgentFactory _agentFactory;
    private readonly IDocumentContentProvider _documentContent;
    private readonly ILogger<TriageExecutor> _logger;

    /// <summary>Initialises a new <see cref="TriageExecutor"/>.</summary>
    /// <param name="agentFactory">
    /// Builds this node's agent, bound to the run's document. Supplied by the host, which is where the
    /// provider is chosen; Core requires only that it satisfies the neutral seam.
    /// </param>
    /// <param name="documentContent">
    /// The run-scoped document-content profile, used to redeem the handle ingest produced. Run-scoped
    /// and memoised per document, so redeeming it re-prepares nothing that was expensive.
    /// </param>
    /// <param name="logger">Sink for this node's structured run events.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public TriageExecutor(
        IDocumentBoundAgentFactory agentFactory,
        IDocumentContentProvider documentContent,
        ILogger<TriageExecutor> logger)
        : base(CbixWorkflowNodes.Triage)
    {
        ArgumentNullException.ThrowIfNull(agentFactory);
        ArgumentNullException.ThrowIfNull(documentContent);
        ArgumentNullException.ThrowIfNull(logger);

        _agentFactory = agentFactory;
        _documentContent = documentContent;
        _logger = logger;
    }

    /// <summary>Runs triage over one ingested document.</summary>
    /// <param name="message">The ingest outcome. Always a new registration - see the remarks.</param>
    /// <param name="context">The workflow context for this superstep.</param>
    /// <param name="cancellationToken">Token that cancels the model call.</param>
    /// <returns>The document paired with the profile triage produced, or with the refusal it earned.</returns>
    /// <remarks>
    /// The edge into this node already filters duplicates, and <see cref="TriagedDocument"/> refuses
    /// one outright. Both, deliberately: the edge is the design (design 5.1 - a duplicate's run stops
    /// at the registry) and the constructor is the backstop, because the failure it prevents is a
    /// model answering confidently about a document nobody prepared, which no downstream assertion
    /// would catch.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="DocumentPreparationException">
    /// The document could not be presented to the model. Deliberately not caught: a document the
    /// active profile cannot present is not a triage outcome, and reporting it as one would put "the
    /// upload failed" and "the model could not identify this" in the same review queue with the same
    /// reason.
    /// </exception>
    public override async ValueTask<TriagedDocument> HandleAsync(
        DocumentIngestResult message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Redeemed from the handle rather than carried from ingest, which is the shape a resumed run
        // needs: DocumentContent holds provider payloads that a checkpoint's JSON round trip silently
        // drops, so the handle is what run state persists and this is where it is turned back into
        // content. On a first run the handle is already in hand and the provider's memo answers
        // immediately; after a resume the same call re-obtains equivalent content without repaying
        // for a model call.
        DocumentContent content = await _documentContent
            .PrepareAsync(message.Submitted, message.ContentHandle, cancellationToken)
            .ConfigureAwait(false);

        BoundDocumentAgent agent = _agentFactory.CreateForDocument(content);

        // Blank as well as null. TriagedDocument refuses a blank agent name outright - it is part of
        // design 8's provenance record - so an agent named "" or "   " (which nothing forbids a host
        // or a provider integration from producing) would turn a successful model call into an
        // ArgumentException thrown after the call was paid for. The fallback is the node id, which is
        // the name the graph knows this agent by anyway.
        string agentName = agent.Agent.Name is { Length: > 0 } name && !string.IsNullOrWhiteSpace(name)
            ? name
            : CbixWorkflowNodes.Triage;

        LogTriageStarting(_logger, message.Registered.DocumentId, agentName, content.Capabilities.ProfileName);

        AgentResponse response = await agent
            .RunAsync(TriagePrompt, cancellationToken)
            .ConfigureAwait(false);

        string reply = response.Text ?? string.Empty;

        if (DocumentProfileParser.TryParse(reply, out DocumentProfile? profile, out DocumentProfileFormatException? refusal))
        {
            LogDocumentProfiled(
                _logger,
                message.Registered.DocumentId,
                agentName,
                profile!.Confidence);

            return new TriagedDocument(message, agentName, reply, profile, profileParseFailure: null);
        }

        // Nothing the model wrote is logged. The refusal's message is written by the parser and names
        // only the contract and, where it needs one, a field name it has already sanitised; the reply
        // itself travels on the message for the review queue and for design 8's provenance record,
        // where it is stored rather than rendered into a log line.
        LogTriageReplyRefused(_logger, message.Registered.DocumentId, agentName, refusal!.Defect);

        return new TriagedDocument(message, agentName, reply, profile: null, profileParseFailure: refusal.Message);
    }

    /// <summary>Structured event marking the run's first model call.</summary>
    /// <remarks>
    /// Emitted before the call rather than after it, because this is the call that primes the prompt
    /// cache: an operator reading a cost anomaly needs the point at which the priming call started,
    /// and a call that never returns logs nothing at all if the event waits for a response. The agent
    /// name is part of design 8's provenance record; the profile name is what tells a reader whether
    /// this run saw the document or only its text, which decides how much the answer is worth.
    /// </remarks>
    [LoggerMessage(
        EventId = CbixEventIds.WorkflowTriageStarting,
        Level = LogLevel.Information,
        Message = "Triage is calling the model. Document={DocumentId}, agent={AgentName}, "
            + "presentation={ProfileName}.")]
    private static partial void LogTriageStarting(
        ILogger logger,
        string documentId,
        string agentName,
        string profileName);

    /// <summary>Structured event for a reply that matched the contract.</summary>
    /// <remarks>
    /// The confidence is logged and the profile's contents are not. Jurisdiction, document type and
    /// layout family are strings a model wrote, and a log line is the one place where an untrusted
    /// string does damage disproportionate to its length - forged lines, reordered text, a broken
    /// sink. They are carried on the message and stored where they can be rendered deliberately;
    /// what an operator needs here is the number the routing edge acts on.
    /// </remarks>
    [LoggerMessage(
        EventId = CbixEventIds.WorkflowDocumentProfiled,
        Level = LogLevel.Information,
        Message = "Triage profiled a document. Document={DocumentId}, agent={AgentName}, "
            + "confidence={Confidence}.")]
    private static partial void LogDocumentProfiled(
        ILogger logger,
        string documentId,
        string agentName,
        double confidence);

    /// <summary>Structured event for a reply the contract refused.</summary>
    /// <remarks>
    /// Warning rather than Error: the document is routed to a human, which is the designed response
    /// (design 9), so nothing is lost - but a rate of these climbing is how a prompt regression, a
    /// model change or a provider that started wrapping its output announces itself, and that is
    /// worth a level an alert rule can key on.
    /// </remarks>
    [LoggerMessage(
        EventId = CbixEventIds.WorkflowTriageReplyRefused,
        Level = LogLevel.Warning,
        Message = "Triage refused the agent's reply: it does not match the DocumentProfile contract. "
            + "Document={DocumentId}, agent={AgentName}, defect={Defect}.")]
    private static partial void LogTriageReplyRefused(
        ILogger logger,
        string documentId,
        string agentName,
        DocumentProfileDefect defect);
}
