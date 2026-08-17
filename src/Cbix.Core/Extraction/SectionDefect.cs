namespace Cbix.Core.Extraction;

/// <summary>
/// How a section agent's reply failed its section contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate vocabulary from <see cref="DocumentProfileDefect"/>, deliberately, and the
/// duplication of a few member names is the smaller cost.</b> The two describe different boundaries
/// with different consumers: a profile defect ends up on a review-queue row an operator filters, while
/// a section defect is what Sprint 02's <c>ValidationReport</c> turns into a targeted retry of one
/// agent. Merging them would produce a union in which half the values are unreachable from either
/// caller, and every future section parser would widen it further - seven agents' failure modes in
/// one enum that nobody can switch on exhaustively.
/// </para>
/// <para>
/// The envelope-specific values below have no counterpart on the profile side at all, which is the
/// clearest evidence the two are different vocabularies rather than one that was copied.
/// </para>
/// </remarks>
public enum SectionDefect
{
    /// <summary>The reply was not a JSON object at all - prose, an apology, or JSON of another shape.</summary>
    NotAJsonObject = 0,

    /// <summary>The reply omitted a field the section contract declares.</summary>
    MissingField = 1,

    /// <summary>The reply carried a field the section contract does not declare.</summary>
    UndeclaredField = 2,

    /// <summary>The reply gave one of the contract's fields more than once.</summary>
    DuplicateField = 3,

    /// <summary>A field or an envelope member was present with the wrong JSON type.</summary>
    WrongType = 4,

    /// <summary>
    /// A field's value was not a provenance envelope at all.
    /// </summary>
    /// <remarks>
    /// Its own value rather than <see cref="WrongType"/>, because it names a specific and likely
    /// mistake: a model that returned bare scalars, having answered the question while ignoring the
    /// contract that makes the answer auditable. The fix is a prompt, not a schema.
    /// </remarks>
    NotAnEnvelope = 5,

    /// <summary>
    /// An envelope carried a value but no page, or a value but no snippet.
    /// </summary>
    /// <remarks>
    /// The grounding gate (design 5.6) checks a snippet against the text layer of the page the
    /// envelope names, so an envelope missing either half is a value the gate cannot defend - and one
    /// that would therefore be published on the strength of nothing.
    /// </remarks>
    UngroundableValue = 6,

    /// <summary>The reported source page is not a logical page number.</summary>
    PageOutOfRange = 7,

    /// <summary>An envelope's confidence was outside the unit interval, or was not a finite number.</summary>
    ConfidenceOutOfRange = 8,

    /// <summary>A date was not an unambiguous ISO 8601 calendar date.</summary>
    /// <remarks>
    /// Refused rather than best-guessed. <c>03/04/2026</c> is two different dates depending on who
    /// wrote it, and an effective date read the wrong way round is a permission that starts eleven
    /// months early.
    /// </remarks>
    MalformedDate = 9,

    /// <summary>A string field or snippet was longer than the parser will accept.</summary>
    ValueTooLong = 10,

    /// <summary>The reply was larger than the parser will read at all.</summary>
    ReplyTooLarge = 11,

    /// <summary>
    /// A declared string field was present but empty or white space.
    /// </summary>
    /// <remarks>
    /// Its own value rather than <see cref="WrongType"/>, which is where it used to bucket: the type
    /// was right and the content was not, and telling an operator "wrong type" about a correctly-typed
    /// string sends them to look at a schema that is working. The prompting rules give a model exactly
    /// one way to say "the document does not state this" - a null - so a blank is a model that
    /// answered without answering.
    /// </remarks>
    BlankValue = 12,

    /// <summary>
    /// The section parsed, but a field that is part of the published key carries no value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raised by the executor rather than by the parser, because it is a statement about the section as
    /// a whole rather than about any one envelope: every individual envelope may be perfectly well
    /// formed, with a null value and honest null provenance, and the section still be unpublishable.
    /// </para>
    /// <para>
    /// <c>doc_ref</c> and <c>doc_version</c> are <c>NOT NULL</c> and together are the primary key of
    /// <c>country_documents</c> (design 6), so a section without them cannot be persisted at all - and
    /// a run that reported <c>Persisted</c> for one would be claiming an outcome the schema forbids.
    /// This guards only the key; full completeness is Sprint 02's validator.
    /// </para>
    /// </remarks>
    MissingKeyValue = 13,
}
