using Cbix.Core.Ingest;

namespace Cbix.Core.Documents;

/// <summary>
/// The degraded fallback profile: the locally extracted text layer and nothing else, reported as
/// degraded (design 5.1, story S01-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this profile is for.</b> A provider or a configuration without vision support can still
/// run: document control, entities, conditions, marketing rules, tax and version history are all
/// read out of text perfectly well. What it cannot do reliably is the permission matrix, because a
/// matrix is a table and a table is geometry - which status code belongs to which product and
/// client category is carried by the row and column a cell sits in, not by the order characters
/// come out of a text extractor.
/// </para>
/// <para>
/// <b>The risk here is silence, not failure</b>, which is why the capability flag matters more than
/// anything else this class does. A text-only run counted alongside a full-fidelity one has its
/// matrix accuracy averaged into the same number, and the resulting metric describes neither
/// (design 5.9, 11). <see cref="Capabilities"/> is what keeps the two distinguishable.
/// </para>
/// <para>
/// <b>The degraded flag is not set here; it is unavoidable.</b>
/// <see cref="DocumentContentCapabilities"/> refuses to construct with
/// <see cref="DocumentContentCapabilities.IncludesVisualContent"/> false and
/// <see cref="DocumentContentCapabilities.IsDegraded"/> false. That invariant exists for exactly
/// this profile, and it is deliberately not this profile's own discipline: a rule enforced by the
/// type cannot be forgotten by the next implementation of the port, and this one's whole purpose is
/// to be honest about being worse.
/// </para>
/// <para>
/// <b>Producing the flag is the whole of this story's scope.</b> Nothing consumes it yet. The eval
/// harness that reads it - and stops averaging a degraded run in with a full-fidelity one - arrives
/// in Sprint 02.
/// </para>
/// <para>
/// <b>Text is still emitted per page, with page markers.</b> Degraded presentation does not make
/// provenance optional: every extracted scalar still carries a <c>SourcePage</c>, and the grounding
/// gate still looks for the snippet on the page the agent named. See
/// <see cref="LocalDocumentContentProfile"/> for the block layout and why the marker is a separate
/// block from the verbatim page text.
/// </para>
/// <para>
/// It takes no renderer, which is more than tidiness: a profile with no way to produce an image
/// cannot emit one by accident, so "contains no image blocks" is a property of its dependencies
/// rather than of its control flow.
/// </para>
/// </remarks>
public sealed class TextOnlyDocumentContentProfile : LocalDocumentContentProfile
{
    /// <summary>
    /// The stable profile name. It is part of the eval harness's measurement record and part of
    /// every handle this profile issues, so it does not change casually.
    /// </summary>
    public const string ProfileName = "text-only";

    /// <summary>Initialises a new <see cref="TextOnlyDocumentContentProfile"/>.</summary>
    /// <param name="textLayerExtractor">Produces the per-page text; see <see cref="LocalDocumentContentProfile"/> for why the port rather than a <see cref="TextLayer"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="textLayerExtractor"/> is <see langword="null"/>.</exception>
    public TextOnlyDocumentContentProfile(ITextLayerExtractor textLayerExtractor)
        : base(textLayerExtractor)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// Note what is <em>not</em> written here: <c>isDegraded: true</c> is passed, but passing
    /// <see langword="false"/> would not compile a different behaviour - it would throw. The
    /// invariant is the guarantee; this argument merely agrees with it.
    /// </remarks>
    protected override DocumentContentCapabilities Capabilities { get; } =
        new(ProfileName, includesVisualContent: false, isDegraded: true);
}
