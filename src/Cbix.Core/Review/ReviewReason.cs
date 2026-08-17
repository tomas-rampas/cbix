namespace Cbix.Core.Review;

/// <summary>
/// Why a document is waiting for a human.
/// </summary>
/// <remarks>
/// <para>
/// <b>A closed set rather than free text, because the queue is worked rather than read.</b> A
/// reviewer facing ten thousand rows needs to sort, filter and count them; a reason that exists only
/// inside a sentence can be read one row at a time and no other way. It is also the measurement the
/// improvement flywheel runs on - design 5.7 makes every correction a golden-set entry, and "which
/// gate sends us the most work" is the question that decides what gets fixed next.
/// </para>
/// <para>
/// The two values here are triage's. Sprint 02 adds the validator's - failed grounding, a broken
/// referential-integrity check, an incomplete matrix - and the normaliser's <c>UNMAPPED</c>. They are
/// numbered with room between the groups for the same reason the event-id blocks are.
/// </para>
/// </remarks>
public enum ReviewReason
{
    /// <summary>
    /// Triage produced a profile, but its confidence was below the routing threshold (design 9,
    /// "New/unknown layout family").
    /// </summary>
    /// <remarks>
    /// The canonical case: a layout family nobody has seen before. The design's own remedy is to add
    /// few-shot examples for the family once a human has confirmed what it is, which is why the row
    /// carries the confidence the model reported rather than only the fact that it was too low.
    /// </remarks>
    TriageLowConfidence = 0,

    /// <summary>
    /// Triage's agent replied with something that is not a <c>DocumentProfile</c> at all.
    /// </summary>
    /// <remarks>
    /// Distinguished from a low confidence deliberately, because the two want different responses. A
    /// low confidence is a statement about the document; a refused reply is a statement about the
    /// model, the prompt or the provider - and a cluster of these is how a prompt regression or a
    /// provider that started wrapping its output announces itself.
    /// </remarks>
    TriageReplyRefused = 1,
}
