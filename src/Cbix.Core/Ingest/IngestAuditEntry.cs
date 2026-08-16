using Cbix.Core.Documents;

namespace Cbix.Core.Ingest;

/// <summary>
/// One append-only record of a document arriving at the ingest gate and what was decided about it.
/// </summary>
/// <remarks>
/// <para>
/// The registry answers "which bytes exist"; this answers "what was submitted, when, from where,
/// and what happened". They are separate on purpose: a duplicate submission must leave the registry
/// row untouched (design 9) while still being visible afterwards, which is only possible if the
/// evidence lands somewhere else.
/// </para>
/// <para>
/// <b>Why the submitted reference is kept whole.</b> The interesting fact about a duplicate is
/// usually not the identity - that matched, by definition - but the difference: the same bytes
/// arriving under a new file name or from a new path is how a re-issued document, a mis-filed copy
/// or a loop in an upstream feed shows itself. Recording only the identity would throw away the
/// part of the event worth reading.
/// </para>
/// </remarks>
public sealed record IngestAuditEntry
{
    /// <summary>Initialises a new <see cref="IngestAuditEntry"/>.</summary>
    /// <param name="eventType">What was decided about the submission.</param>
    /// <param name="contentHash">Hash of the submitted bytes.</param>
    /// <param name="submitted">
    /// The document as submitted on this occasion, which on a duplicate may differ from the
    /// registered one in every respect except identity.
    /// </param>
    /// <param name="recordedUtc">When this event was recorded. Must be a UTC instant.</param>
    /// <param name="firstRegisteredUtc">
    /// When these bytes were first registered - equal to <paramref name="recordedUtc"/> for a first
    /// submission, and earlier than it for a duplicate. Must be a UTC instant, and must not be later
    /// than <paramref name="recordedUtc"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="contentHash"/> or <paramref name="submitted"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="eventType"/> is not a defined value; <paramref name="submitted"/>'s identity
    /// does not match <paramref name="contentHash"/>; either timestamp is not UTC; or
    /// <paramref name="firstRegisteredUtc"/> is later than <paramref name="recordedUtc"/>.
    /// </exception>
    public IngestAuditEntry(
        IngestAuditEventType eventType,
        ContentHash contentHash,
        DocumentReference submitted,
        DateTimeOffset recordedUtc,
        DateTimeOffset firstRegisteredUtc)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        ArgumentNullException.ThrowIfNull(submitted);

        if (!Enum.IsDefined(eventType))
        {
            throw new ArgumentException(
                $"'{eventType}' is not a defined ingest audit event type.",
                nameof(eventType));
        }

        if (!string.Equals(submitted.DocumentId, contentHash.Canonical, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The submitted document identity '{submitted.DocumentId}' is not derived from the content hash '{contentHash.Canonical}'.",
                nameof(submitted));
        }

        if (recordedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"The recorded timestamp must be UTC; its offset is {recordedUtc.Offset}.",
                nameof(recordedUtc));
        }

        if (firstRegisteredUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"The first-registered timestamp must be UTC; its offset is {firstRegisteredUtc.Offset}.",
                nameof(firstRegisteredUtc));
        }

        if (firstRegisteredUtc > recordedUtc)
        {
            throw new ArgumentException(
                $"The first-registered timestamp {firstRegisteredUtc:O} is later than the recorded timestamp {recordedUtc:O}: "
                    + "a document cannot have been registered after the submission that observed it.",
                nameof(firstRegisteredUtc));
        }

        EventType = eventType;
        ContentHash = contentHash;
        Submitted = submitted;
        RecordedUtc = recordedUtc;
        FirstRegisteredUtc = firstRegisteredUtc;
    }

    /// <summary>Gets what was decided about the submission.</summary>
    public IngestAuditEventType EventType { get; }

    /// <summary>Gets the hash of the submitted bytes, whose canonical form is the document's identity.</summary>
    public ContentHash ContentHash { get; }

    /// <summary>Gets the document as submitted on this occasion.</summary>
    public DocumentReference Submitted { get; }

    /// <summary>Gets the instant this event was recorded, in UTC.</summary>
    public DateTimeOffset RecordedUtc { get; }

    /// <summary>Gets the instant these bytes were first registered, in UTC.</summary>
    public DateTimeOffset FirstRegisteredUtc { get; }
}
