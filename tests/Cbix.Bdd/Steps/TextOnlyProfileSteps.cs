using Cbix.Bdd.Support;
using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/TextOnlyDocumentContentProfile.feature</c> (story S01-07).
/// <para>
/// The profile is constructed reflectively and then used through the port; see
/// <see cref="LocalDocumentProfileFixture"/> for why. As with S01-06 the collaborator is the
/// production PDFPig extractor running against the real DE specimen, so "contains no image blocks"
/// is a statement about content built from a real document rather than about an empty stub.
/// </para>
/// </summary>
[Binding]
public sealed class TextOnlyProfileSteps(TextOnlyProfileState state)
{
    [Given("the DE specimen's PDFPig text layer")]
    public async Task GivenTheDeSpecimensPdfPigTextLayer()
    {
        (DocumentReference document, TextLayer textLayer) = await LocalDocumentProfileFixture.RegisterSpecimenAsync();

        state.Document = document;
        state.TextLayer = textLayer;

        DocumentIngestOptions ingestOptions = LocalDocumentProfileFixture.CreateIngestOptions();

        ITextLayerExtractor extractor = new PdfPigTextLayerExtractor(
            ingestOptions,
            NullLogger<PdfPigTextLayerExtractor>.Instance);

        // One collaborator, and that is itself part of the story: this profile takes no renderer,
        // so it cannot produce visual content even by accident.
        state.Profile = (IDocumentContentProvider)LocalDocumentProfileFixture.CreateCoreInstance(
            "TextOnlyDocumentContentProfile",
            extractor);
    }

    [When("the text-only profile prepares document content")]
    public async Task WhenTheTextOnlyProfilePreparesDocumentContent()
    {
        IDocumentContentProvider profile = state.Profile
            ?? throw new InvalidOperationException("The Given step did not construct the profile.");
        DocumentReference document = state.Document
            ?? throw new InvalidOperationException("The Given step did not register the specimen.");

        state.Content = await profile.PrepareAsync(document);
    }

    [Then("the content contains no image blocks")]
    public void ThenTheContentContainsNoImageBlocks()
    {
        DocumentContent content = RequireContent();
        TextLayer textLayer = RequireTextLayer();

        // No image blocks by any route: not a DataContent carrying pixels, and not a hosted image
        // referenced by uri either. Asserting only on DataContent would leave the second open.
        Assert.Empty(content.Content.OfType<DataContent>());
        Assert.Empty(content.Content.OfType<UriContent>());

        // And this is not vacuous - the profile did produce content, and the content is the
        // document's text. A profile that returned nothing would also contain no image blocks.
        Assert.NotEmpty(content.Content);
        Assert.All(content.Content, block => Assert.IsType<TextContent>(block));

        List<string> texts = [.. content.Content.Cast<TextContent>().Select(block => block.Text)];

        for (int page = TextLayer.FirstLogicalPageNumber; page <= textLayer.PageCount; page++)
        {
            // Verbatim, for the reason S01-06 records: this is the corpus the grounding gate does
            // string containment against in Sprint 02, so a tidied rendering of it would fail
            // snippets the model copied correctly.
            Assert.True(
                texts.Contains(textLayer.GetPage(page), StringComparer.Ordinal),
                $"No text block carries logical page {page} verbatim as the extractor produced it.");

            // The page number is still stated even though there is no image to pair it with -
            // SourcePage provenance does not become optional because the presentation is degraded.
            Assert.Contains(
                texts,
                text => text.Contains($"page {page}", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(text, textLayer.GetPage(page), StringComparison.Ordinal));
        }
    }

    // The colon is escaped: an unescaped ':' is fine in a Cucumber Expression, but the quoted
    // phrase is passed as a {string} parameter so the scenario reads as the story wrote it while
    // the binding still asserts on the flag itself rather than on the sentence.
    [Then("the returned capability flag reports {string}")]
    public void ThenTheReturnedCapabilityFlagReports(string claim)
    {
        DocumentContent content = RequireContent();

        Assert.Equal("visual content: false", claim);
        Assert.False(
            content.Capabilities.IncludesVisualContent,
            "The text-only profile reported that it presents the document visually.");

        // The name is part of the eval harness's measurement record (design 5.9), so a run's
        // metrics can be attributed to the profile that produced them. The handle carries the same
        // name because a resume must be routed back to the profile that issued it.
        Assert.Equal("text-only", content.Capabilities.ProfileName);
        Assert.Equal(content.Capabilities.ProfileName, content.Handle.ProfileName);

        // Nothing remote to redeem: a resume re-prepares from the local file.
        Assert.Null(content.Handle.ProviderToken);
    }

    [Then("the run is marked degraded because the capability type refuses any other answer")]
    public void ThenTheRunIsMarkedDegradedBecauseTheCapabilityTypeRefusesAnyOtherAnswer()
    {
        DocumentContent content = RequireContent();

        Assert.True(
            content.Capabilities.IsDegraded,
            "Content with no visual representation was not reported as degraded, so a text-only run would be counted as a full-fidelity one.");

        // The point of the story, and the reason the invariant lives in DocumentContentCapabilities
        // rather than in this profile: the flag is not set correctly here because someone
        // remembered to set it, it is set correctly because the type will not accept the
        // alternative. Constructing the same profile's capabilities with degraded off is refused.
        ArgumentException refusal = Assert.Throws<ArgumentException>(
            () => new DocumentContentCapabilities(
                content.Capabilities.ProfileName,
                includesVisualContent: false,
                isDegraded: false));

        Assert.Equal("isDegraded", refusal.ParamName);

        // Consumed by nobody yet, and that is the story's stated scope: S01-07 guarantees the flag
        // is produced. The eval harness that reads it arrives in Sprint 02 (design 5.9), which is
        // where a degraded run stops being averaged in with a full-fidelity one.
    }

    private DocumentContent RequireContent() =>
        state.Content ?? throw new InvalidOperationException("The When step did not prepare any content.");

    private TextLayer RequireTextLayer() =>
        state.TextLayer ?? throw new InvalidOperationException("The Given step did not capture the text layer.");
}
