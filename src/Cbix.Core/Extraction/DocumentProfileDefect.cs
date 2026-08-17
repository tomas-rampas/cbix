namespace Cbix.Core.Extraction;

/// <summary>
/// How a triage reply failed the <see cref="DocumentProfile"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>An enum rather than a message, because something has to route on it.</b> The review-queue row
/// a refused reply produces (design 6, story S01-15) has to say why the document is there in a form
/// an operator can filter and count: "the model returned prose" and "the model invented a field" are
/// different problems with different fixes - the first is usually a prompt or a provider that
/// wraps its output, the second is usually a schema drift. A free-text message distinguishes them
/// to a human reading one row and to nobody reading ten thousand.
/// </para>
/// <para>
/// The values deliberately describe the <em>reply</em>, never the document. A malformed reply says
/// nothing about the PDF, and a queue that mixed the two would send reviewers hunting a document
/// problem that does not exist.
/// </para>
/// </remarks>
public enum DocumentProfileDefect
{
    /// <summary>
    /// The reply was not a JSON object at all - prose, an apology, a partial answer, or JSON of some
    /// other shape.
    /// </summary>
    NotAJsonObject = 0,

    /// <summary>The reply omitted a field the contract declares.</summary>
    MissingField = 1,

    /// <summary>
    /// The reply carried a field the contract does not declare.
    /// </summary>
    /// <remarks>
    /// Refused rather than ignored. An extra field means the model answered a different question
    /// from the one that was asked - most often because a prompt and a schema have drifted apart -
    /// and quietly discarding it would hide the drift until something downstream needed the field
    /// that was actually meant.
    /// </remarks>
    UndeclaredField = 2,

    /// <summary>A field was present with the wrong JSON type.</summary>
    WrongType = 3,

    /// <summary>A declared string field was present but empty or white space.</summary>
    /// <remarks>
    /// A blank is not a value and it is not an honest "unknown" either: the prompt asks for the
    /// literal <c>UNKNOWN</c> with a lowered confidence when the document does not state something,
    /// which routes to review. A blank would travel as data.
    /// </remarks>
    BlankValue = 4,

    /// <summary>Confidence was outside the unit interval, or was not a finite number.</summary>
    ConfidenceOutOfRange = 5,

    /// <summary>
    /// The reply gave one of the contract's fields more than once.
    /// </summary>
    /// <remarks>
    /// <b>Its own value rather than folded into <see cref="MissingField"/>, and the reason is what
    /// this enum is for.</b> An operator counting defects is asking which failure to fix, and these
    /// two point in opposite directions: a missing field is a model that did not answer, a repeated
    /// one is a model that answered twice - usually a retry or a streaming artefact stitched together
    /// wrongly. Bucketing the second under the first would send someone to look at a prompt that is
    /// working.
    /// </remarks>
    DuplicateField = 6,

    /// <summary>
    /// A declared string field was longer than the parser will accept.
    /// </summary>
    /// <remarks>
    /// Nothing legitimate approaches the cap: these are a document type, a country code, a reference,
    /// a version and a layout-family name copied out of a document. A value past it is a model that
    /// pasted a page into a field, or output that is not a profile at all - and either way it must not
    /// travel to a log line, a review-queue row or a database column sized for the real thing.
    /// </remarks>
    ValueTooLong = 7,

    /// <summary>
    /// The reply was larger than the parser will read at all.
    /// </summary>
    /// <remarks>
    /// Refused before parsing rather than after, because the cost being avoided is the parse itself.
    /// See <see cref="DocumentProfileParser"/> for why a provider's output-token setting is not a
    /// substitute for this bound.
    /// </remarks>
    ReplyTooLarge = 8,
}
