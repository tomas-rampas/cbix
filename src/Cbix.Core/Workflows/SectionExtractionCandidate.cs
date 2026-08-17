using Cbix.Core.Extraction;

namespace Cbix.Core.Workflows;

/// <summary>
/// What a section extractor proposes for one document - the "agents propose" half of the governing
/// principle, on its way to the code that disposes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Named a candidate, not a result, and the word is doing work.</b> Nothing on this type has been
/// validated: no schema conformance beyond the parse, no grounding of snippets against the PDFPig
/// text layer, no referential integrity, no completeness count. It is a proposal. Sprint 02's
/// validator is the sole authority that turns one into something publishable, and calling this a
/// result would be the first step towards someone persisting it.
/// </para>
/// <para>
/// <b>One typed section today, and the shape says so honestly.</b> Story S01-16 puts the real
/// DocControl agent in the extractor slot, so this carries its <see cref="DocControlSection"/> as a
/// named property rather than as an entry in a collection of one. Sprint 02 widens the node into the
/// seven-way fan-out and this becomes the fan-in aggregate - at which point the property becomes one
/// of seven and <see cref="SectionCount"/> becomes a real count. A speculative collection invented
/// here would be a list that is always length one, asserted about as if it could be anything else.
/// </para>
/// </remarks>
public sealed class SectionExtractionCandidate
{
    /// <summary>Initialises a new <see cref="SectionExtractionCandidate"/>.</summary>
    /// <param name="document">The triaged document these sections were extracted from.</param>
    /// <param name="docControl">The document-control block the DocControl agent proposed.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public SectionExtractionCandidate(TriagedDocument document, DocControlSection docControl)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(docControl);

        Document = document;
        DocControl = docControl;
    }

    /// <summary>Gets the triaged document these sections were extracted from.</summary>
    public TriagedDocument Document { get; }

    /// <summary>Gets the document-control block the DocControl agent proposed (design 5.4).</summary>
    public DocControlSection DocControl { get; }

    /// <summary>
    /// Gets how many section candidates this carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One, by construction, for as long as the graph holds one section agent - a constant rather than
    /// a computed length, because a collection that is always length one would be asserted about as if
    /// it could be anything else. Sprint 02's fan-in makes it a real count.
    /// </para>
    /// <para>
    /// <b>It carries no guarantee on its own, and an earlier note here implied otherwise.</b> A
    /// constant cannot be zero, so the persist step reading it proves nothing by itself. What actually
    /// guarantees a section reached persist is the pair of checks around it: this type's
    /// <c>ThrowIfNull</c> on <see cref="DocControl"/>, so a candidate cannot exist without one, and the
    /// executor refusing before it constructs one - so a run reporting the <c>Persisted</c> disposition
    /// has, necessarily, a parsed section behind it. The number is a report, not the control.
    /// </para>
    /// </remarks>
    public int SectionCount => 1;
}
