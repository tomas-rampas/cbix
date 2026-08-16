using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.AI;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// Story S01-07. The shared port contract is inherited; what is added here is the one thing that is
/// this profile's own - that it sends no visual content and is honest about what that costs.
/// </summary>
public sealed class TextOnlyDocumentContentProfileTests : LocalDocumentContentProfileContract
{
    /// <inheritdoc />
    protected override string ExpectedProfileName => TextOnlyDocumentContentProfile.ProfileName;

    /// <inheritdoc />
    protected override bool ExpectsVisualContent => false;

    /// <inheritdoc />
    protected override LocalDocumentContentProfile CreateProfile(ITextLayerExtractor textLayerExtractor) =>
        new TextOnlyDocumentContentProfile(textLayerExtractor);

    [Fact]
    public void Constructor_NullExtractor_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new TextOnlyDocumentContentProfile(null!));

    [Fact]
    public async Task Capabilities_ReportNoVisualContentAndDegraded()
    {
        // Read off the prepared content, not off the profile: see the note on the generic-vision
        // equivalent for why Capabilities is protected.
        TextOnlyDocumentContentProfile profile = new(new StubTextLayerExtractor());

        DocumentContent content = await profile.PrepareAsync(TestDocuments.Create());

        Assert.Equal("text-only", content.Capabilities.ProfileName);
        Assert.False(content.Capabilities.IncludesVisualContent);
        Assert.True(content.Capabilities.IsDegraded);
    }

    [Fact]
    public async Task PrepareAsync_EmitsTextBlocksOnly()
    {
        StubTextLayerExtractor extractor = new("page one", "page two");
        TextOnlyDocumentContentProfile profile = new(extractor);

        DocumentContent content = await profile.PrepareAsync(TestDocuments.Create());

        // Marker and text per page, and nothing else. Asserting the total rather than only the
        // absence of images keeps this from passing if a future block type were added silently.
        Assert.Equal(4, content.Content.Count);
        Assert.All(content.Content, block => Assert.IsType<TextContent>(block));
        Assert.Empty(content.Content.OfType<DataContent>());
        Assert.Empty(content.Content.OfType<UriContent>());
    }

    [Fact]
    public async Task PrepareAsync_StillStatesThePageNumberOfEveryPage()
    {
        // Degraded presentation does not make provenance optional. Every extracted scalar still
        // carries a SourcePage, and the grounding gate still looks on the page the agent named - so
        // the model still has to be told which page it is reading.
        StubTextLayerExtractor extractor = new("alpha", "beta", "gamma");
        TextOnlyDocumentContentProfile profile = new(extractor);

        DocumentContent content = await profile.PrepareAsync(TestDocuments.Create());

        List<string> texts = [.. content.Content.Cast<TextContent>().Select(block => block.Text)];

        Assert.Contains("--- Page 1 of 3 ---", texts, StringComparer.Ordinal);
        Assert.Contains("--- Page 2 of 3 ---", texts, StringComparer.Ordinal);
        Assert.Contains("--- Page 3 of 3 ---", texts, StringComparer.Ordinal);

        // And the marker is never fused onto the text the model is instructed to copy verbatim.
        Assert.Contains("alpha", texts, StringComparer.Ordinal);
        Assert.DoesNotContain(texts, text => text.Contains("Page 1", StringComparison.Ordinal) && text.Contains("alpha", StringComparison.Ordinal));
    }
}
