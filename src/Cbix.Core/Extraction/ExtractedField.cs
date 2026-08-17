namespace Cbix.Core.Extraction;

/// <summary>
/// One extracted scalar together with the evidence for it: where it was read, the verbatim text it
/// was read from, and how certain the model was (design 5.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape is design 5.4's, verbatim, and this is the one type in the solution where that
/// matters most.</b> The design declares
/// <c>ExtractedField&lt;T&gt;(T? Value, int? SourcePage, string? SourceSnippet, double Confidence)</c>,
/// and this record is that declaration. Story S01-16's Done-means says why: Sprint 02's grounding
/// gate consumes this shape unchanged, and every published value's row in <c>field_provenance</c>
/// (design 6) is built from it. A member renamed, reordered or made non-nullable here is a change to
/// the audit trail of everything the pipeline will ever publish.
/// </para>
/// <para>
/// <b>The envelope is always present; the VALUE is what may be absent.</b> That asymmetry is the
/// point of wrapping at all. The prompting rules say to return null for a field the document does not
/// state rather than inventing one (CLAUDE.md, design 5.4) - and an absence still has provenance: the
/// page that was read, the confidence that nothing was there. A section that dropped the whole
/// envelope for an absent field would lose the difference between "the document does not say" and
/// "nobody looked".
/// </para>
/// <para>
/// <b><see cref="Confidence"/> is the one member that is not nullable</b>, and that is the design's
/// own choice rather than an oversight: a model that produced a field at all produced it with some
/// certainty, so there is no such thing as a field with no confidence. Design 11's caution applies to
/// what the number means, not to whether it exists - it is not to be trusted at face value, and the
/// thresholds compared against it are calibrated from observed correction rates.
/// </para>
/// <para>
/// <b>Provenance travels with the value rather than beside it.</b> A parallel structure - values in
/// one object, provenance in another - is the arrangement in which the two drift, and design 8's bar
/// is that any published value answers in one query which document and page it came from, what the
/// source text said, which model produced it and how confident it was. Keeping them in one record
/// makes losing half of that a compile error rather than a mapping bug.
/// </para>
/// </remarks>
/// <typeparam name="T">
/// The extracted value's type. A nullable value type is the way to express an optional scalar - for
/// example <c>ExtractedField&lt;DateOnly?&gt;</c> - because <c>T?</c> on an unconstrained type
/// parameter carries no nullability for value types, so <c>ExtractedField&lt;DateOnly&gt;</c> could
/// not represent an absent date at all.
/// </typeparam>
/// <param name="Value">The extracted value, or <see langword="null"/> when the document does not state one.</param>
/// <param name="SourcePage">
/// The logical page the value was read from, as shown in a PDF viewer, or <see langword="null"/> when
/// there is no value to attribute.
/// </param>
/// <param name="SourceSnippet">
/// Text copied verbatim from the document, which the validator's grounding gate checks for by
/// ordinal containment in the PDFPig text layer of <paramref name="SourcePage"/> (design 5.6). It is
/// a quotation, not a paraphrase: a snippet that has been tidied, re-cased or re-punctuated fails
/// that gate exactly as an invented one does, which is the intended behaviour.
/// </param>
/// <param name="Confidence">How certain the model reports being, in the unit interval.</param>
public sealed record ExtractedField<T>(
    T? Value,
    int? SourcePage,
    string? SourceSnippet,
    double Confidence);
