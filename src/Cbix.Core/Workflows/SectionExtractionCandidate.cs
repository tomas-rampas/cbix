namespace Cbix.Core.Workflows;

/// <summary>
/// What a section extractor proposes for one document - the "agents propose" half of the governing
/// principle, on its way to the code that disposes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Named a candidate, not a result, and the word is doing work.</b> Nothing on this type has been
/// validated: no schema conformance, no grounding of snippets against the PDFPig text layer, no
/// referential integrity, no completeness count. It is a proposal. Sprint 02's validator is the sole
/// authority that turns one into something publishable, and calling this a result would be the first
/// step towards someone persisting it.
/// </para>
/// <para>
/// <b>What the sections field becomes.</b> S01-16 puts the real DocControl agent in the extractor
/// slot and this carries its <c>DocControlSection</c>; Sprint 02 widens the slot to the seven-way
/// fan-out and this becomes the fan-in aggregate. The list shape is chosen now for that reason - the
/// aggregate is a list of section candidates, so the shape does not change when the count does.
/// </para>
/// </remarks>
public sealed class SectionExtractionCandidate
{
    /// <summary>Initialises a new <see cref="SectionExtractionCandidate"/>.</summary>
    /// <param name="document">The triaged document these sections were extracted from.</param>
    /// <param name="sections">
    /// The section candidates, keyed by section name. Empty is legal and is what the S01-13 stub
    /// produces: the topology story proves the slot is wired and reached, not that anything was
    /// extracted through it.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public SectionExtractionCandidate(TriagedDocument document, IReadOnlyDictionary<string, string> sections)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sections);

        Document = document;
        Sections = sections;
    }

    /// <summary>Gets the triaged document these sections were extracted from.</summary>
    public TriagedDocument Document { get; }

    /// <summary>
    /// Gets the raw section candidates, keyed by section name.
    /// </summary>
    /// <remarks>
    /// Typed as strings for exactly as long as there is no typed section to carry: S01-16 lands
    /// <c>DocControlSection</c> with its <c>ExtractedField&lt;T&gt;</c> provenance envelopes, and this
    /// becomes a typed collection then. A speculative typed shape invented here would have to match
    /// design 5.4 exactly to be worth anything, and matching it is that story's job.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Sections { get; }
}
