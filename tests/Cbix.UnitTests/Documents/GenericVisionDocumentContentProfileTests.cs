using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.AI;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// Story S01-06. The shared port contract is inherited; what is added here is the part that is
/// genuinely this profile's own - that it sends page images, one per page, paired with the right
/// page, and that it says so in its capabilities.
/// </summary>
public sealed class GenericVisionDocumentContentProfileTests : LocalDocumentContentProfileContract
{
    /// <inheritdoc />
    protected override string ExpectedProfileName => GenericVisionDocumentContentProfile.ProfileName;

    /// <inheritdoc />
    protected override bool ExpectsVisualContent => true;

    /// <inheritdoc />
    protected override LocalDocumentContentProfile CreateProfile(ITextLayerExtractor textLayerExtractor)
    {
        // The renderer is sized from whatever the inherited test's extractor produces. This profile
        // reconciles the two page counts and fails when they disagree - correct behaviour, asserted
        // below in its own test - so a fixed-size renderer here would make the inherited tests that
        // vary the page count fail for a reason that has nothing to do with what they assert.
        StubPageImageRenderer renderer = new(
            () => textLayerExtractor is StubTextLayerExtractor stub ? stub.PageCount : 1);

        return new GenericVisionDocumentContentProfile(textLayerExtractor, renderer);
    }

    [Fact]
    public void Constructor_NullRenderer_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new GenericVisionDocumentContentProfile(new StubTextLayerExtractor(), null!));

    [Fact]
    public void Constructor_NullExtractor_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => new GenericVisionDocumentContentProfile(null!, new StubPageImageRenderer(1)));

    [Fact]
    public async Task Capabilities_ReportVisualContentAndNotDegraded()
    {
        // Read off the prepared content rather than off the profile instance. Capabilities is
        // protected now: it is the extensibility contract, and a caller that interrogated a profile
        // about what it can do would be branching on the profile, which the port forbids. This is
        // the copy that provably describes the content in hand.
        GenericVisionDocumentContentProfile profile = new(new StubTextLayerExtractor(), new StubPageImageRenderer(1));

        DocumentContent content = await profile.PrepareAsync(TestDocuments.Create());

        Assert.Equal("generic-vision", content.Capabilities.ProfileName);
        Assert.True(content.Capabilities.IncludesVisualContent);

        // Not degraded, and this is a recorded decision rather than an omission: design 5.1 makes
        // the text-only profile the degraded case, and whether locally rendered images are
        // materially worse than a provider's native PDF mode is a measurement the Sprint 02 eval
        // harness makes per profile. Claiming degraded now would answer that question in advance
        // and route every fallback run to review on no evidence.
        Assert.False(content.Capabilities.IsDegraded);
    }

    [Fact]
    public async Task PrepareAsync_EmitsOneImagePerPageImmediatelyAfterThatPagesText()
    {
        StubTextLayerExtractor extractor = new("page one", "page two");
        StubPageImageRenderer renderer = new(2);
        GenericVisionDocumentContentProfile profile = new(extractor, renderer);

        DocumentContent content = await profile.PrepareAsync(TestDocuments.Create());

        // Marker, text, image - three blocks per page, in that order. Content blocks carry no page
        // number of their own, so adjacency is the only thing telling a model which image belongs
        // to which page.
        Assert.Equal(6, content.Content.Count);
        Assert.IsType<TextContent>(content.Content[0]);
        Assert.Equal("page one", Assert.IsType<TextContent>(content.Content[1]).Text);
        Assert.IsType<DataContent>(content.Content[2]);
        Assert.IsType<TextContent>(content.Content[3]);
        Assert.Equal("page two", Assert.IsType<TextContent>(content.Content[4]).Text);
        Assert.IsType<DataContent>(content.Content[5]);

        // The right image, not merely an image: the stub encodes the page number in its last byte,
        // so a profile that paired images by list position rather than by logical page number would
        // fail here.
        Assert.Equal(1, Assert.IsType<DataContent>(content.Content[2]).Data.Span[^1]);
        Assert.Equal(2, Assert.IsType<DataContent>(content.Content[5]).Data.Span[^1]);

        Assert.All(
            content.Content.OfType<DataContent>(),
            image => Assert.True(image.HasTopLevelMediaType("image")));
    }

    [Fact]
    public async Task PrepareAsync_RendersOncePerDocumentHoweverManyCallers()
    {
        // The expensive half of this profile, and the reason PDFium's global lock does not serialise
        // the section fan-out: six of the seven agents hit the memo instead of queueing behind a
        // render they do not need.
        StubTextLayerExtractor extractor = new("page one");
        StubPageImageRenderer renderer = new(1);
        GenericVisionDocumentContentProfile profile = new(extractor, renderer);
        DocumentReference document = TestDocuments.Create();

        for (int caller = 0; caller < 7; caller++)
        {
            await profile.PrepareAsync(document);
        }

        Assert.Equal(1, renderer.CallCount);
        Assert.Equal(1, extractor.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_WhenTheTwoLocalReadersDisagreeAboutPageCount_Fails()
    {
        // Two local components reading the same file and reaching different answers about how many
        // pages it has. Presenting the smaller count would drop a page - possibly the one carrying
        // the matrix - while still reporting full visual fidelity for the run.
        StubTextLayerExtractor extractor = new("page one", "page two", "page three");
        StubPageImageRenderer renderer = new(2);
        GenericVisionDocumentContentProfile profile = new(extractor, renderer);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => profile.PrepareAsync(TestDocuments.Create()));

        Assert.False(error.IsTransient);
        Assert.Contains("3 text pages but 2 rendered pages", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_WhenTheRenderFails_IsClassifiedLikeAnyOtherLocalFailure()
    {
        StubTextLayerExtractor extractor = new("page one");
        StubPageImageRenderer renderer = new(1)
        {
            Failure = () => new DocumentNotIngestibleException(DocumentNotIngestibleReason.Unreadable, "/documents/manual.pdf"),
        };

        GenericVisionDocumentContentProfile profile = new(extractor, renderer);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => profile.PrepareAsync(TestDocuments.Create()));

        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task PrepareAsync_WhenAProfileClaimsVisualContentButEmitsNone_Fails()
    {
        // The silent mis-pairing of the two protected hooks. RenderPagesAsync and AppendPageContent
        // are overridden independently, so a profile can render every page, throw the pixels away,
        // and hand back text-only content while its capabilities still report full visual fidelity.
        // Nothing downstream would notice - the eval harness would credit that run's matrix accuracy
        // to a visual profile, which is exactly the mis-measurement the degraded flag exists to
        // prevent. It has to fail loudly at the point of construction instead.
        HalfOverriddenProfile profile = new(new StubTextLayerExtractor("page one"), new StubPageImageRenderer(1));

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => profile.PrepareAsync(TestDocuments.Create()));

        Assert.False(error.IsTransient);
        Assert.Contains("carries no visual block", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_WhenTheRasteriserFaults_IsDistinctFromADocumentRefusal()
    {
        // The classification MAJOR-2 asks for, asserted at the profile boundary. A fault inside the
        // native renderer and a refusal of the document both end the run without retries, so it
        // would be easy to collapse them - and wrong. They mean different things to a human: one
        // says a supplier sent a bad file, the other says a native library the dependency audit
        // cannot see just failed on untrusted input. What preserves the distinction downstream is
        // the inner exception type, so that is what this pins.
        StubTextLayerExtractor extractor = new("page one");
        StubPageImageRenderer renderer = new(1)
        {
            Failure = () => new PageRenderFaultException("/documents/manual.pdf", "the rasteriser yielded no bitmap"),
        };

        GenericVisionDocumentContentProfile profile = new(extractor, renderer);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => profile.PrepareAsync(TestDocuments.Create()));

        // Not transient: a renderer that has just faulted will not succeed on an immediate retry,
        // and retrying against it turns one hostile document into sustained pressure on the host.
        Assert.False(error.IsTransient);

        Assert.IsType<PageRenderFaultException>(error.InnerException);
        Assert.Contains("rasteriser faulted", error.Message, StringComparison.Ordinal);

        // And specifically NOT recorded as the document being unreadable, which is the mistake the
        // separate type exists to prevent.
        Assert.IsNotType<DocumentNotIngestibleException>(error.InnerException);
    }

    [Fact]
    public async Task PrepareAsync_WhenTheShareFailsDuringTheRender_IsTransient()
    {
        StubTextLayerExtractor extractor = new("page one");
        StubPageImageRenderer renderer = new(1)
        {
            Failure = () => new IOException("the share dropped mid-render"),
        };

        GenericVisionDocumentContentProfile profile = new(extractor, renderer);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => profile.PrepareAsync(TestDocuments.Create()));

        Assert.True(error.IsTransient);
    }

    /// <summary>
    /// A profile that renders pages and then never emits them - the exact half-override the base
    /// class's post-condition exists to catch.
    /// </summary>
    /// <remarks>
    /// Written as a deliberate mistake rather than described in a comment, because the defect being
    /// guarded against is one a future contributor makes by accident and the only convincing proof
    /// the guard works is making it.
    /// </remarks>
    private sealed class HalfOverriddenProfile(ITextLayerExtractor extractor, IPageImageRenderer renderer)
        : LocalDocumentContentProfile(extractor)
    {
        protected override DocumentContentCapabilities Capabilities { get; } =
            new("half-overridden", includesVisualContent: true, isDegraded: false);

        protected override async Task<IReadOnlyList<PageImage>?> RenderPagesAsync(
            DocumentReference document,
            CancellationToken cancellationToken) =>
            await renderer.RenderAsync(document, cancellationToken);

        // AppendPageContent deliberately NOT overridden.
    }
}
