using Cbix.Core.Extraction;
using Cbix.Core.Ingest;

namespace Cbix.Core.Workflows;

/// <summary>
/// What triage hands the rest of the graph: the ingested document, what the triage agent said about
/// it, and the <see cref="DocumentProfile"/> that reply parsed into - or the refusal it earned.
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
/// <b><see cref="Profile"/> and <see cref="ProfileParseFailure"/> are exactly one of two states, and
/// the constructor enforces it.</b> Either the agent's reply matched design Appendix A's contract and
/// there is a profile, or it did not and there is a refusal saying which part it broke. A message
/// carrying both would let a consumer read the profile and ignore the refusal; a message carrying
/// neither would be a document nothing can route. The routing edges (story S01-15) read
/// <see cref="RequiresReview"/>, which folds the two states into the one question the graph asks.
/// </para>
/// <para>
/// <b><see cref="AgentResponse"/> is kept verbatim even when it parsed cleanly.</b> Design 8's audit
/// bar is that a published value can be traced to what produced it - which model, which prompt, what
/// it actually said - and a parsed record cannot answer the last of those. It is also what makes a
/// refusal reviewable: an operator looking at a queued document needs the reply, not a paraphrase of
/// why it was rejected.
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
    /// <param name="profile">
    /// The profile the answer parsed into, or <see langword="null"/> when it was refused.
    /// </param>
    /// <param name="profileParseFailure">
    /// Why the answer was refused, or <see langword="null"/> when it parsed. Safe to log and to store:
    /// <see cref="DocumentProfileParser"/> never echoes an unsanitised model-written value into one.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="agentResponse"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="agentName"/> is empty or white space; <paramref name="document"/> is a
    /// duplicate submission; or <paramref name="profile"/> and <paramref name="profileParseFailure"/>
    /// are both supplied or both absent. A duplicate's run stops at the registry (design 5.1) and
    /// carries neither text layer nor content handle, so triaging one means an agent was asked about a
    /// document that was never prepared - the run this refusal exists to make impossible.
    /// </exception>
    public TriagedDocument(
        DocumentIngestResult document,
        string agentName,
        string agentResponse,
        DocumentProfile? profile,
        string? profileParseFailure)
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

        if (profile is not null && profileParseFailure is not null)
        {
            throw new ArgumentException(
                "A triaged document cannot carry both a profile and a refusal: a consumer reading the "
                    + "profile would act on data the pipeline had already declined.",
                nameof(profileParseFailure));
        }

        if (profile is null && string.IsNullOrWhiteSpace(profileParseFailure))
        {
            throw new ArgumentException(
                "A triaged document with no profile must say why. A document that is neither profiled nor "
                    + "refused cannot be routed: it would reach extraction on a confidence nobody computed.",
                nameof(profileParseFailure));
        }

        Document = document;
        AgentName = agentName;
        AgentResponse = agentResponse;
        Profile = profile;
        ProfileParseFailure = profileParseFailure;
    }

    /// <summary>Gets the ingest outcome: the document reference, its registry entry, its text layer and its content handle.</summary>
    public DocumentIngestResult Document { get; }

    /// <summary>Gets the name of the agent that produced <see cref="AgentResponse"/>.</summary>
    public string AgentName { get; }

    /// <summary>Gets the triage agent's verbatim answer.</summary>
    public string AgentResponse { get; }

    /// <summary>
    /// Gets the profile design 5.3 routes on, or <see langword="null"/> when the agent's reply did
    /// not match the contract.
    /// </summary>
    public DocumentProfile? Profile { get; }

    /// <summary>
    /// Gets why the agent's reply was refused, or <see langword="null"/> when it parsed into
    /// <see cref="Profile"/>.
    /// </summary>
    public string? ProfileParseFailure { get; }

    /// <summary>
    /// Reports whether this document must go to a human rather than into extraction.
    /// </summary>
    /// <param name="reviewConfidenceThreshold">
    /// The confidence at or above which extraction proceeds; see
    /// <see cref="TriageRoutingOptions.ReviewConfidenceThreshold"/>.
    /// </param>
    /// <returns><see langword="true"/> when the document belongs in the review queue.</returns>
    /// <remarks>
    /// <para>
    /// <b>Both ways of not knowing land here, and that is the point.</b> Design 9's "New/unknown
    /// layout family" row answers a low triage confidence by routing the whole document to review; a
    /// reply that did not parse at all is strictly less information than a low-confidence one, so it
    /// takes the same edge. Treating an unparseable reply as a failure instead would put a routine,
    /// expected outcome on the exception path and train operators to ignore failures.
    /// </para>
    /// <para>
    /// The boundary is inclusive on the extraction side: a document whose confidence exactly equals
    /// the threshold proceeds, so the configured number reads as "the lowest confidence that still
    /// extracts" rather than as a value whose own meaning depends on an operator remembering which
    /// side of it they fall on.
    /// </para>
    /// <para>
    /// <b>Written as the negation of "confident enough" rather than as "not confident enough", and
    /// the two are not the same predicate.</b> They agree for every valid confidence; they differ on
    /// NaN, where every comparison is false - so <c>Confidence &lt; threshold</c> would answer "does
    /// not require review" and send a document nobody could score into extraction. The parser refuses
    /// a non-finite confidence today, which means this is a second guard rather than the only one, and
    /// that is exactly why it is written this way: a routing gate whose safety depends on a check in a
    /// different class stops being safe the day that class changes. Fail closed, locally.
    /// </para>
    /// </remarks>
    public bool RequiresReview(double reviewConfidenceThreshold) =>
        Profile is not { } profile || !(profile.Confidence >= reviewConfidenceThreshold);
}
