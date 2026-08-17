namespace Cbix.Core.Extraction;

/// <summary>
/// The document-control block of a country instruction: what the document says about itself
/// (design 5.4's <c>DocControlSection</c>, design 6's <c>country_documents</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The field set is design 6's <c>country_documents</c>, minus the columns nobody extracts.</b>
/// That table is the published home of this section, so its columns are the specification for what
/// has to be read out of the document: <c>doc_ref</c>, <c>doc_version</c>, <c>country_iso</c>,
/// <c>doc_status</c>, <c>effective_date</c>, <c>review_date</c>, <c>supersedes_ref</c> and
/// <c>document_owner</c>. <c>extraction_run_id</c>, <c>valid_from</c> and <c>valid_to</c> are absent
/// on purpose: they are facts about the RUN and the validity window, written by the persist step
/// (design 5.8), and a section agent that proposed them would be proposing something no document
/// contains.
/// </para>
/// <para>
/// <b>Every scalar is enveloped, with no exceptions and no "obvious" ones.</b> It is tempting to
/// leave the country code bare - triage already reported a jurisdiction - but design 8's bar is that
/// ANY published value answers in one query which page it came from and what the source text said,
/// and an unenveloped field is a published value with no answer. The envelope is also what makes the
/// grounding gate total: it checks every snippet it is given, so a field that carries none is a field
/// it cannot defend.
/// </para>
/// <para>
/// <b>Dates are <see cref="DateOnly"/>, parsed at the boundary rather than carried as text.</b>
/// Design 6 types them as SQL <c>date</c> and design 5.6's completeness gate compares them
/// ("effective date must precede review date"), so a string would push the parse - and its failure
/// mode - into the validator, where a malformed date would surface as a completeness failure rather
/// than as the malformed date it is. The type argument is nullable because the envelope's
/// <c>T?</c> carries no nullability for a value type; see <see cref="ExtractedField{T}"/>.
/// </para>
/// <para>
/// <b>This is a candidate, not a record of fact.</b> Nothing here has been grounded, checked for
/// referential integrity or counted. Sprint 02's validator is the sole authority that turns one into
/// something publishable - the governing principle is that agents propose and code disposes, and this
/// type is the proposal.
/// </para>
/// </remarks>
/// <param name="DocRef">The document's own reference identifier - half of the published key.</param>
/// <param name="Version">The document's own version designation - the other half of the published key.</param>
/// <param name="CountryIso">The ISO 3166-1 alpha-2 code of the jurisdiction the document governs.</param>
/// <param name="Status">The document's lifecycle status as it states it, for example <c>Approved</c>.</param>
/// <param name="EffectiveDate">The date the instruction takes effect.</param>
/// <param name="ReviewDate">The date the instruction is next due for review, when it states one.</param>
/// <param name="Supersedes">The prior document this one replaces, when it states one.</param>
/// <param name="Owner">The owning function named in the document-control block.</param>
public sealed record DocControlSection(
    ExtractedField<string> DocRef,
    ExtractedField<string> Version,
    ExtractedField<string> CountryIso,
    ExtractedField<string> Status,
    ExtractedField<DateOnly?> EffectiveDate,
    ExtractedField<DateOnly?> ReviewDate,
    ExtractedField<string> Supersedes,
    ExtractedField<string> Owner);
