using Cbix.Core.Ingest;

namespace Cbix.Core.Workflows;

/// <summary>
/// What triage hands the extraction stage: the ingested document, plus what the triage agent said
/// about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The ingest result travels with the profile, and that is the whole reason this type exists.</b>
/// Everything downstream needs the document - the text layer for grounding, the content handle to
/// show a model - and MAF routes messages by CLR type, so an edge carrying only a profile would leave
/// every later executor re-reading state to find the document it is talking about. Carrying both is
/// one object and removes that class of mistake.
/// </para>
/// <para>
/// <b><see cref="AgentResponse"/> is a seam, not a design.</b> Story S01-14 replaces it with the
/// <c>DocumentProfile</c> record of design Appendix A - doc type, jurisdiction ISO, doc reference,
/// version, layout family, confidence - parsed from the agent's structured output. S01-13 owns the
/// topology, not the contract: the executor slot exists, an injected <see cref="Microsoft.Agents.AI.AIAgent"/>
/// fills it, and what it returns is carried verbatim rather than invented into a shape that would
/// have to be replaced anyway.
/// </para>
/// <para>
/// <b>What S01-15 adds, and why it needs nothing new here.</b> Its conditional edge routes low
/// confidence and unrecognised documents to the review queue. That is a predicate over this message
/// - once the profile lands, <c>profile.Confidence</c> and <c>profile.DocType</c> are on it - so the
/// routing story adds edges to the graph and touches no node and no message type.
/// </para>
/// </remarks>
public sealed class TriagedDocument
{
    /// <summary>Initialises a new <see cref="TriagedDocument"/>.</summary>
    /// <param name="document">The ingest outcome this profile describes.</param>
    /// <param name="agentName">
    /// The name of the agent that produced <paramref name="agentResponse"/>. Part of the provenance
    /// record design 8 requires: which model and prompt produced a value is not answerable later if
    /// nothing recorded it at the time.
    /// </param>
    /// <param name="agentResponse">The triage agent's verbatim answer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="agentResponse"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="agentName"/> is empty or white space, or <paramref name="document"/> is a
    /// duplicate submission. A duplicate's run stops at the registry (design 5.1) and carries neither
    /// text layer nor content handle, so triaging one means an agent was asked about a document that
    /// was never prepared - the run this refusal exists to make impossible.
    /// </exception>
    public TriagedDocument(DocumentIngestResult document, string agentName, string agentResponse)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(agentResponse);

        if (!document.IsNewRegistration)
        {
            throw new ArgumentException(
                "A duplicate submission cannot be triaged: its run stops at the registry, so nothing was "
                    + "prepared for it and any agent asked about it would be answering about a document it "
                    + "was never shown.",
                nameof(document));
        }

        Document = document;
        AgentName = agentName;
        AgentResponse = agentResponse;
    }

    /// <summary>Gets the ingest outcome: the document reference, its registry entry, its text layer and its content handle.</summary>
    public DocumentIngestResult Document { get; }

    /// <summary>Gets the name of the agent that produced <see cref="AgentResponse"/>.</summary>
    public string AgentName { get; }

    /// <summary>Gets the triage agent's verbatim answer. Story S01-14 replaces this with a parsed <c>DocumentProfile</c>.</summary>
    public string AgentResponse { get; }
}
