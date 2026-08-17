namespace Cbix.Core.Review;

/// <summary>
/// One document waiting for a human: the in-process counterpart of a row in design 6's
/// <c>review_queue</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>It carries what a reviewer needs to start, and nothing that would let them start wrongly.</b>
/// The document identity, why it is here, what the pipeline was told, and when - and no extracted
/// data, because there is none: a document routed by triage never reached a section agent. The
/// review UI (Sprint 04) locates the source by the document identity, exactly as design 8 requires.
/// </para>
/// <para>
/// <b>The class-versus-record choice follows the house rule.</b> Get-only properties and no
/// <c>init</c> setters, because a <c>with</c> expression bypasses the constructor and the constructor
/// is where the bounds below are enforced.
/// </para>
/// </remarks>
public sealed class ReviewQueueEntry
{
    /// <summary>
    /// The longest <see cref="Detail"/> this type will carry.
    /// </summary>
    /// <remarks>
    /// Matched to the <c>nvarchar(1000)</c> in <c>db/schema/review_queue.sql</c> deliberately. A bound
    /// enforced only at the database is a bound discovered in Sprint 03, on a live queue, as a failed
    /// insert on the document that most needed a human; enforcing it here means the truncation is a
    /// decision made where the text is built rather than an error thrown where it is stored.
    /// </remarks>
    public const int MaxDetailLength = 1000;

    /// <summary>
    /// The longest <see cref="DocumentId"/> this type will carry.
    /// </summary>
    /// <remarks>
    /// Matched to <c>review_queue.document_id</c> and, through it, to
    /// <c>document_registry.document_id</c> - the column this one has a foreign key to, so a value
    /// that will not fit here cannot be a registered document in the first place. The application mints
    /// exactly one shape of id (<c>sha256:</c> plus 64 hex characters, 71 in all), so the bound leaves
    /// room for a longer digest name without leaving room for a free-form string.
    /// </remarks>
    public const int MaxDocumentIdLength = 80;

    /// <summary>
    /// The longest <see cref="AgentName"/> this type will carry.
    /// </summary>
    /// <remarks>
    /// Matched to <c>review_queue.agent_name</c>. The same reasoning as <see cref="MaxDetailLength"/>,
    /// and it applies for the same reason it applies there: a bound enforced only at the database is a
    /// bound discovered in Sprint 03, on a live queue, as a failed insert on the document that most
    /// needed a human.
    /// </remarks>
    public const int MaxAgentNameLength = 120;

    /// <summary>Initialises a new <see cref="ReviewQueueEntry"/>.</summary>
    /// <param name="documentId">
    /// The registry identity of the document - the canonical <c>sha256:...</c> content hash, which is
    /// what every other table joins on and the one document identifier that carries nothing an outside
    /// party chose.
    /// </param>
    /// <param name="reason">Why the document is here.</param>
    /// <param name="detail">
    /// A description safe to store and to render: written by CBIX, never a verbatim model reply. Where
    /// it quotes anything a model wrote, the quoting code sanitises it first.
    /// </param>
    /// <param name="agentName">The agent whose output produced this row, for design 8's provenance record.</param>
    /// <param name="reportedConfidence">
    /// The confidence triage reported, or <see langword="null"/> when there was no parseable profile to
    /// take one from. Nullable rather than defaulted to zero: "the model was not confident" and "the
    /// model said nothing usable" are different rows, and a zero would merge them.
    /// </param>
    /// <param name="queuedUtc">When the row was written, from the injected clock.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="documentId"/>, <paramref name="detail"/> or <paramref name="agentName"/> is
    /// empty or white space, or <paramref name="detail"/> exceeds <see cref="MaxDetailLength"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reportedConfidence"/> is supplied and is not a finite value in the unit
    /// interval.
    /// </exception>
    public ReviewQueueEntry(
        string documentId,
        ReviewReason reason,
        string detail,
        string agentName,
        double? reportedConfidence,
        DateTimeOffset queuedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        if (documentId.Length > MaxDocumentIdLength)
        {
            throw new ArgumentException(
                $"The review-queue document id is {documentId.Length} characters; the column holds "
                    + $"{MaxDocumentIdLength}, and it has a foreign key to the registry - so a value this "
                    + "long is not a registered document at all.",
                nameof(documentId));
        }

        if (detail.Length > MaxDetailLength)
        {
            throw new ArgumentException(
                $"The review-queue detail is {detail.Length} characters; the column holds "
                    + $"{MaxDetailLength}. Truncate it deliberately where it is built rather than "
                    + "discovering the bound as a failed insert.",
                nameof(detail));
        }

        if (agentName.Length > MaxAgentNameLength)
        {
            throw new ArgumentException(
                $"The review-queue agent name is {agentName.Length} characters; the column holds "
                    + $"{MaxAgentNameLength}.",
                nameof(agentName));
        }

        if (reportedConfidence is { } confidence
            && (!double.IsFinite(confidence) || confidence < 0d || confidence > 1d))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reportedConfidence),
                confidence,
                "A reported confidence must be a finite value in the unit interval; the parser refuses "
                    + "anything else, so a value outside it here means something built a row from an "
                    + "unvalidated reply.");
        }

        DocumentId = documentId;
        Reason = reason;
        Detail = detail;
        AgentName = agentName;
        ReportedConfidence = reportedConfidence;
        QueuedUtc = queuedUtc;
    }

    /// <summary>Gets the registry identity of the document waiting for a human.</summary>
    public string DocumentId { get; }

    /// <summary>Gets why the document is here.</summary>
    public ReviewReason Reason { get; }

    /// <summary>Gets the description a reviewer reads first.</summary>
    public string Detail { get; }

    /// <summary>Gets the name of the agent whose output produced this row.</summary>
    public string AgentName { get; }

    /// <summary>Gets the confidence triage reported, or <see langword="null"/> when there was no profile.</summary>
    public double? ReportedConfidence { get; }

    /// <summary>Gets when the row was written.</summary>
    public DateTimeOffset QueuedUtc { get; }
}
