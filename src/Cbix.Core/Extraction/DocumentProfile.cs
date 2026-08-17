namespace Cbix.Core.Extraction;

/// <summary>
/// What triage concluded about one document: its type, the jurisdiction it governs, its own
/// reference and version, the layout family it belongs to, and how certain the model was
/// (design 5.3, design Appendix A).
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape is design Appendix A's, verbatim, and it is not a place to be creative.</b> The
/// appendix declares
/// <c>DocumentProfile(string DocType, string JurisdictionIso, string DocRef, string Version,
/// string LayoutFamily, double Confidence)</c>, and this record is that declaration. Structured
/// output means a contract two independent parties agree on - the prompt that asks for it and the
/// parser that reads it - so a field added, renamed or made optional here silently changes what the
/// model is being asked for.
/// </para>
/// <para>
/// <b>Every member is a value, not an option, and that is deliberate for triage specifically.</b>
/// The uniform prompting rules (CLAUDE.md) say to return null for an absent field rather than invent
/// one, and the section agents obey that literally through <c>ExtractedField&lt;T&gt;</c>. Triage is
/// the one agent whose output is pure routing: a null jurisdiction and a null document type are not
/// data to be recorded, they are the statement "I could not identify this", and the design already
/// has a channel for that - a low <see cref="Confidence"/>, which routes the whole document to
/// review rather than into extraction (design 9, "New/unknown layout family"). So the prompt asks
/// for the literal string <c>UNKNOWN</c> in a field it cannot read, paired with a confidence that
/// says so, and the parser refuses a blank. The alternative - nullable fields - would let a
/// confidently-null profile through the routing edge and into extraction, which is the failure the
/// routing exists to prevent.
/// </para>
/// <para>
/// <b>Nothing validates in this type.</b> Construction from parsed JSON is
/// <see cref="DocumentProfileParser"/>'s job, and it is strict there rather than here so that the
/// record stays exactly the appendix's line and so a refusal can name <em>which</em> part of the
/// contract a reply broke. A validating constructor could only throw
/// <c>ArgumentException</c>, which tells a review queue nothing.
/// </para>
/// </remarks>
/// <param name="DocType">The kind of document, as the document itself names it.</param>
/// <param name="JurisdictionIso">The ISO 3166-1 alpha-2 code of the country the document governs.</param>
/// <param name="DocRef">The document's own reference identifier.</param>
/// <param name="Version">The document's own version designation.</param>
/// <param name="LayoutFamily">
/// A stable name for the document's layout, which later stories use to select prompt variants
/// (design 5.3). An unrecognised family is the design's canonical review trigger, so this value is
/// routing metadata rather than extracted data.
/// </param>
/// <param name="Confidence">
/// How certain the model reports being, in the unit interval. Read as a routing signal and nothing
/// more: design 11 warns plainly that model-reported confidence is not to be trusted at face value,
/// and the threshold it is compared against is calibrated against observed correction rates rather
/// than chosen.
/// </param>
public sealed record DocumentProfile(
    string DocType,
    string JurisdictionIso,
    string DocRef,
    string Version,
    string LayoutFamily,
    double Confidence);
