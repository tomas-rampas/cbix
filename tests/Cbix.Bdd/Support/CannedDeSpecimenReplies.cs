using System.Text.Json.Nodes;

namespace Cbix.Bdd.Support;

/// <summary>
/// The canned agent replies the offline lanes hand the Sprint 01 graph for the DE specimen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared because they must agree, not merely because they repeat.</b> Every feature that runs the
/// whole graph now needs two replies - triage's profile and the DocControl section - and each is
/// parsed strictly against its own contract. Two copies would drift the moment a contract gained a
/// field, and the symptom would be one feature's scenarios failing for a reason that has nothing to do
/// with what they assert.
/// </para>
/// <para>
/// <b>The DocControl snippets are real.</b> Every one was taken from the actual PDFPig extraction of
/// the committed DE specimen and verified as an exact ordinal substring of page 1. That is what makes
/// the grounding assertion in <c>DocControlExtraction.feature</c> a test of string containment rather
/// than a fixture agreeing with itself: change one character here and the grounding scenario fails,
/// which is precisely the behaviour Sprint 02's validator will rely on.
/// </para>
/// </remarks>
public static class CannedDeSpecimenReplies
{
    /// <summary>Triage's reply: design Appendix A's profile, above the routing threshold.</summary>
    /// <param name="jurisdiction">The ISO code the profile reports.</param>
    /// <param name="docRef">The document reference the profile reports.</param>
    /// <param name="version">The version the profile reports.</param>
    /// <param name="confidence">The confidence the profile reports.</param>
    /// <returns>The JSON reply.</returns>
    public static string TriageProfile(
        string jurisdiction = "DE",
        string docRef = "CBTI-DE-2026-011",
        string version = "3.2",
        double confidence = 0.97d) =>
        new JsonObject
        {
            ["DocType"] = "Cross-Border Trading Legal Instruction",
            ["JurisdictionIso"] = jurisdiction,
            ["DocRef"] = docRef,
            ["Version"] = version,
            ["LayoutFamily"] = "contoso-country-manual-v4",
            ["Confidence"] = confidence,
        }.ToJsonString();

    /// <summary>
    /// The DocControl section for the DE specimen, with real verbatim snippets from page 1.
    /// </summary>
    /// <param name="docRefSnippet">
    /// Overrides the DocRef snippet, so the grounding feature's negative control can fabricate one.
    /// </param>
    /// <returns>The JSON reply.</returns>
    public static string DocControlSection(string? docRefSnippet = null)
    {
        static JsonObject Envelope(string? value, string snippet, double confidence) => new()
        {
            ["Value"] = value,
            ["SourcePage"] = 1,
            ["SourceSnippet"] = snippet,
            ["Confidence"] = confidence,
        };

        return new JsonObject
        {
            ["DocRef"] = Envelope("CBTI-DE-2026-011", docRefSnippet ?? "Document Reference CBTI-DE-2026-011", 0.98d),
            ["Version"] = Envelope("3.2", "Version / Status 3.2 / Approved", 0.97d),
            ["CountryIso"] = Envelope("DE", "Jurisdiction Federal Republic of Germany (DE)", 0.99d),
            ["Status"] = Envelope("Approved", "Version / Status 3.2 / Approved", 0.96d),
            ["EffectiveDate"] = Envelope("2026-08-16", "Effective Date 2026-08-16", 0.98d),
            ["ReviewDate"] = Envelope("2027-08-15", "Next Review Date 2027-08-15", 0.97d),
            ["Supersedes"] = Envelope("CBTI-DE-2023-007 (v3.1)", "Supersedes CBTI-DE-2023-007 (v3.1)", 0.95d),
            ["Owner"] = Envelope(
                "Legal - Cross-Border Trading Office, EMEA",
                "Document Owner Legal - Cross-Border Trading Office, EMEA",
                0.94d),
        }.ToJsonString();
    }
}
