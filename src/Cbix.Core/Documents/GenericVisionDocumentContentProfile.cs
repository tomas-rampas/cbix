using Cbix.Core.Ingest;

using Microsoft.Extensions.AI;

namespace Cbix.Core.Documents;

/// <summary>
/// The provider-agnostic fallback profile: the locally extracted text layer plus locally rendered
/// page images, sent as ordinary multimodal content (design 5.1, story S01-06).
/// </summary>
/// <remarks>
/// <para>
/// <b>What this profile is for.</b> The Claude native-PDF profile is the default and does its page
/// rendering server-side. Every other provider needs the same two representations produced here
/// instead, and this profile is what makes "a provider swap is configuration, not code" true for
/// the hardest part of the pipeline: the permission matrix is a visual table, so a fallback that
/// sent text alone would not be a fallback, it would be the degraded path.
/// </para>
/// <para>
/// <b>Nothing here is provider-specific, and that is the acceptance criterion, not a side effect.</b>
/// No Files API, no upload, no <c>cache_control</c>, no <c>AIContent.RawRepresentation</c> payload -
/// only <see cref="TextContent"/> and <see cref="DataContent"/>, the neutral currency every
/// <c>IChatClient</c> speaks. This profile is the one that has to work when no provider SDK is
/// present at all, which is why the S01-06 scenario walks its surface and its emitted blocks against
/// an assembly allowlist rather than grepping for a vendor name.
/// </para>
/// <para>
/// <b>Presentation order is the association.</b> Blocks are emitted page by page - marker, page
/// text, page image - so each image sits next to the text of the page it renders. Content blocks
/// carry no page number of their own, so ordering is the only thing that tells a model which image
/// goes with which page, and grouping all the text and then all the images would leave it to infer
/// the pairing.
/// </para>
/// <para>
/// <b>Reported as visual and NOT degraded, deliberately, and revisitable.</b> Design 5.1 makes the
/// text-only profile the degraded case and this one the agnosticism fallback, and
/// <see cref="DocumentContentCapabilities"/> permits a visual profile to still call itself degraded
/// - so the honest question is whether locally rendered images are materially worse than a
/// provider's native PDF mode for reading the matrix. Nobody knows yet: that is a measurement, and
/// the eval harness (Sprint 02, design 5.9) reports matrix cell accuracy per profile precisely so it
/// can be made on numbers. Claiming degraded now would suppress that comparison by declaring its
/// answer in advance, and would put every fallback run into review for a reason nobody had
/// evidence for. If the harness shows this profile behind the native one, this flag is the thing
/// that changes.
/// </para>
/// <para>
/// Lifetime, memoisation, handle semantics and failure classification are
/// <see cref="LocalDocumentContentProfile"/>'s; read them there.
/// </para>
/// </remarks>
public sealed class GenericVisionDocumentContentProfile : LocalDocumentContentProfile
{
    /// <summary>
    /// The stable profile name. It is part of the eval harness's measurement record and part of
    /// every handle this profile issues, so it does not change casually.
    /// </summary>
    public const string ProfileName = "generic-vision";

    private readonly IPageImageRenderer _pageImageRenderer;

    /// <summary>Initialises a new <see cref="GenericVisionDocumentContentProfile"/>.</summary>
    /// <param name="textLayerExtractor">Produces the per-page text; see <see cref="LocalDocumentContentProfile"/> for why the port rather than a <see cref="TextLayer"/>.</param>
    /// <param name="pageImageRenderer">Rasterises the pages this profile presents visually.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public GenericVisionDocumentContentProfile(
        ITextLayerExtractor textLayerExtractor,
        IPageImageRenderer pageImageRenderer)
        : base(textLayerExtractor)
    {
        ArgumentNullException.ThrowIfNull(pageImageRenderer);

        _pageImageRenderer = pageImageRenderer;
    }

    /// <inheritdoc />
    protected override DocumentContentCapabilities Capabilities { get; } =
        new(ProfileName, includesVisualContent: true, isDegraded: false);

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<PageImage>?> RenderPagesAsync(
        DocumentReference document,
        CancellationToken cancellationToken) =>
        await _pageImageRenderer.RenderAsync(document, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    protected override void AppendPageContent(
        IList<AIContent> content,
        int logicalPageNumber,
        IReadOnlyDictionary<int, PageImage>? pageImages)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (pageImages is null || !pageImages.TryGetValue(logicalPageNumber, out PageImage? image))
        {
            // Unreachable while the base class's page-count reconciliation holds, and thrown rather
            // than skipped anyway. Silently emitting a page with no image would produce content that
            // reads as a successful visual presentation while one page of the document - possibly
            // the one carrying the matrix - was never shown to the model.
            throw new DocumentPreparationException(
                $"No rendered image was produced for logical page {logicalPageNumber}.",
                isTransient: false);
        }

        content.Add(new DataContent(image.Content, image.MediaType));
    }
}
