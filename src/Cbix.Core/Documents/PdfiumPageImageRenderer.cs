using System.Drawing;
using System.Globalization;
using System.Runtime.Versioning;

using Cbix.Core.Diagnostics;
using Cbix.Core.Ingest;

using Microsoft.Extensions.Logging;

using PDFtoImage;
using PDFtoImage.Exceptions;

using SkiaSharp;

namespace Cbix.Core.Documents;

/// <summary>
/// Renders document pages to PNG images with PDFium, locally and offline - the visual half of the
/// generic-vision profile (design 5.1, story S01-06).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second PDF library.</b> PDFPig parses a PDF; it does not draw one. The fallback profile
/// exists so a provider without a native PDF mode can still read the permission matrix, and reading
/// a table means seeing it, so something has to turn pages into pixels. PDFium is the renderer
/// Chrome ships, which is the reason to prefer it here over a reimplementation: Sprint 02's Matrix
/// agent reads 8-10pt type out of table cells, and rendering quality is paid for directly in matrix
/// cell accuracy, the PoC's headline metric.
/// </para>
/// <para>
/// <b>This type is the pipeline's largest trust boundary, and it is worth naming plainly.</b> It
/// takes a file supplied by an outside party and feeds it to Chromium-derived <em>native</em> code
/// (design 11 addendum). Three consequences follow, and each is handled somewhere below rather than
/// hoped away:
/// </para>
/// <para>
/// <b>1. The document controls the allocation.</b> Page geometry and page count come from the file,
/// not from configuration - <c>Dpi</c> only multiplies what the file asks for. A measured 434-byte
/// PDF declaring a 200 x 200 inch page rasterises to 900 megapixels, 3.35 GB, at the default DPI,
/// and a 1.05 MB PDF declares 5000 pages. Both are refused before any pixel is drawn, by the
/// ceilings on <see cref="PageImageRenderOptions"/>, checked here in plain code.
/// </para>
/// <para>
/// <b>2. A native fault is not a data-quality outcome.</b> An unexpected exception crossing back
/// out of PDFium or Skia is classified as <see cref="PageRenderFaultException"/> and logged as a
/// structured event, never filed as "this document is unreadable". See the classification below for
/// which shapes are which and why the distinction is not cosmetic.
/// </para>
/// <para>
/// <b>3. The worst case is not catchable at all.</b> A memory-safety defect in PDFium can manifest
/// as an access violation, which on .NET terminates the process rather than raising an exception any
/// <c>catch</c> here would see. The blast radius is therefore worker-process death; MAF superstep
/// checkpointing is what bounds the data loss to the current step, and out-of-process rasterisation
/// is the recorded escalation if PDFium exposure grows (design 11 addendum). Nothing in this class
/// mitigates that case - it is bounded by the architecture around it, and stating so here is more
/// use than a mitigation that does not exist.
/// </para>
/// <para>
/// <b>Linux is not an afterthought.</b> CI runs on <c>ubuntu-latest</c> and the worker deploys to
/// Kubernetes, so a rasteriser whose native library loads only on the developer's machine would be
/// a gate that passes locally and a service that fails in production. Measured, not assumed:
/// <c>PdfiumPageImageRendererTests</c> was run against the DE specimen inside a stock
/// <c>mcr.microsoft.com/dotnet/sdk:10.0</c> container (Ubuntu 24.04, x86_64, glibc 2.39) and passed,
/// the render producing PNGs for both pages. No apt package was installed first: the transitive
/// <c>SkiaSharp.NativeAssets.Linux.NoDependencies</c> is the self-contained Skia build, so nothing
/// on the image has to provide fontconfig. One caveat on that measurement - the container carries
/// SDK 10.0.400 rather than the 10.0.110 <c>global.json</c> pins, because no image ships the exact
/// patch; the loading of a native library is not a function of the SDK's feature band, but the
/// difference is recorded rather than glossed.
/// </para>
/// <para>
/// <b>PDFium is not thread-safe.</b> The wrapper serialises every call into the native library
/// behind its own lock, so concurrent renders do not corrupt each other - they queue. That is also
/// why rendering is memoised one level up: the seven-way section fan-out asks for the same document
/// seven times, and without a memo six of those would queue behind the first for no benefit. See
/// <see cref="LocalDocumentContentProfile"/>.
/// </para>
/// <para>
/// <b>The document is parsed twice, not N times, and not once.</b> Precision matters here because
/// an earlier revision of this sentence claimed "once" and was wrong: there are two crossings, one
/// in <c>GetPageSizes</c> to read the geometry the ceilings are applied to, and one in
/// <c>ToImagesAsync</c> to draw the pages. What changed - and it is a security fix rather than a
/// tidy-up - is the growth rate. The per-page <c>SavePng(Stream, ...)</c> overload this replaced
/// re-loaded and re-parsed the whole document for <em>every page</em>, so a hostile N-page file
/// bought N parses. It is now a constant two, whatever N is. The second parse is the price of
/// refusing before allocating, and it is worth paying: reading a page table is cheap next to
/// rasterising one.
/// </para>
/// <para>
/// <b>On <c>UseTiling</c>, decided rather than left open.</b> PDFtoImage offers it to work around
/// corrupted output on very large renders. It is deliberately <em>not</em> enabled: with the
/// per-page megapixel ceiling in force, the sizes that provoke the corruption are refused before
/// they reach the renderer, so tiling would only add a second rendering path - more native surface,
/// exercised by nothing this pipeline accepts. The residual is a deployment that raises
/// <c>MaxPageMegapixels</c> a long way and then meets the corruption on a page the ceiling now
/// admits; that is a configuration change which should be accompanied by revisiting this decision,
/// which is why the decision is written down instead of merely being the default.
/// </para>
/// </remarks>
public sealed partial class PdfiumPageImageRenderer : IPageImageRenderer
{
    /// <summary>The media type every render produces.</summary>
    /// <remarks>
    /// PNG rather than JPEG, and it is not a matter of taste. JPEG's block transform smears the
    /// high-contrast edges that thin table rules and small glyphs are made of, which is precisely
    /// the content the matrix agent has to read; the artefacts land hardest on exactly the pixels
    /// that carry the information. PNG is lossless, so the image the model sees is the image PDFium
    /// drew.
    /// </remarks>
    public const string PageImageMediaType = "image/png";

    /// <summary>PDF user-space units per inch. Page geometry is reported in these, DPI is per inch, and the ratio between them is the render scale.</summary>
    private const double PdfPointsPerInch = 72.0;

    private readonly DocumentIngestOptions _ingestOptions;
    private readonly PageImageRenderOptions _renderOptions;
    private readonly ILogger<PdfiumPageImageRenderer> _logger;

    /// <summary>Initialises a new <see cref="PdfiumPageImageRenderer"/>.</summary>
    /// <param name="ingestOptions">
    /// The ingest gate's own options. Taken whole rather than as a copied size limit, and this is
    /// the same reasoning <see cref="PdfPigTextLayerExtractor"/> records: this renderer reopens a
    /// file the gate already bounded, re-applies that bound to the handle the gate never saw, and
    /// sharing the options object is what stops the two bounds drifting apart as configuration
    /// changes.
    /// </param>
    /// <param name="renderOptions">How finely to rasterise, and the resource ceilings that constrains.</param>
    /// <param name="logger">
    /// Sink for the structured events this type emits.
    /// <para>
    /// Present here although an earlier revision argued it should not be, and the earlier argument
    /// was wrong. It reasoned that a render failure is a data-quality outcome that needs no
    /// instrument. That is true of a damaged document; it is not true of a resource-ceiling refusal
    /// or of a fault crossing back out of native code, both of which are security-relevant signals
    /// about untrusted input and about a library whose defects the dependency audit cannot see. They
    /// need the same treatment ingest refusals get: an event at the moment of refusal, because a
    /// refusal that is only an exception is invisible until something happens to catch it.
    /// </para>
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public PdfiumPageImageRenderer(
        DocumentIngestOptions ingestOptions,
        PageImageRenderOptions renderOptions,
        ILogger<PdfiumPageImageRenderer> logger)
    {
        ArgumentNullException.ThrowIfNull(ingestOptions);
        ArgumentNullException.ThrowIfNull(renderOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _ingestOptions = ingestOptions;
        _renderOptions = renderOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PageImage>> RenderAsync(
        DocumentReference document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        // A real check, not a formality to quiet CA1416. PDFium is native code shipped per runtime
        // identifier, and the platforms CBIX actually runs on - Windows for development, linux-x64
        // for CI and the Kubernetes worker (design 5.2), macOS for developers - are a subset of the
        // ones the wrapper is annotated for. Somewhere outside that set the failure would otherwise
        // be a DllNotFoundException from the loader, at render time, naming a library nobody
        // configured; this makes it a sentence an operator can act on. It also narrows the platform
        // set for the analyzer, which is why the calls below need no suppression.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return RenderPagesAsync(document, cancellationToken);
        }

        // RuntimeIdentifier and OSDescription, not Environment.OSVersion.Platform. That property
        // reports PlatformID.Unix for Android - the one platform in reach that this build genuinely
        // has no binary for - so the message would name the platform as the thing that passes.
        throw new PlatformNotSupportedException(
            "Local page rasterisation needs the PDFium native library, which this build ships for Windows, Linux and macOS only; "
                + $"the current runtime is '{System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}' "
                + $"({System.Runtime.InteropServices.RuntimeInformation.OSDescription}).");
    }

    /// <summary>Does the actual rasterisation, on a platform PDFium ships a native library for.</summary>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private async Task<IReadOnlyList<PageImage>> RenderPagesAsync(
        DocumentReference document,
        CancellationToken cancellationToken)
    {
        string documentPath = document.Location.LocalPath;

        // Opened here rather than left to the renderer so the size bound is applied to a handle we
        // hold, and so a missing file or a dead share surfaces as the FileNotFoundException /
        // IOException the port promises rather than as whatever the native layer reports.
        using FileStream content = OpenForReading(documentPath);

        if (content.Length > _ingestOptions.MaxDocumentBytes)
        {
            throw Refuse(
                DocumentNotIngestibleReason.TooLarge,
                documentPath,
                "the reopened document exceeds the configured maximum document size",
                innerException: null);
        }

        // Everything from here to the render loop is a plain-code gate on numbers the DOCUMENT
        // supplies. It runs before a single pixel is allocated, which is the whole point: by the
        // time a bitmap exists, the decision to allocate it has already been made.
        IList<SizeF> pageSizes = ReadPageGeometry(documentPath, content);

        if (pageSizes.Count < 1)
        {
            throw Refuse(DocumentNotIngestibleReason.Unreadable, documentPath, "the document reports no pages", innerException: null);
        }

        if (pageSizes.Count > _renderOptions.MaxPageCount)
        {
            throw Refuse(
                DocumentNotIngestibleReason.TooLarge,
                documentPath,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the document declares {pageSizes.Count} pages, above the configured ceiling of {_renderOptions.MaxPageCount}"),
                innerException: null);
        }

        double scale = _renderOptions.Dpi / PdfPointsPerInch;
        double totalPixels = 0;

        for (int index = 0; index < pageSizes.Count; index++)
        {
            SizeF page = pageSizes[index];

            // Computed in double and compared as a long. A page's declared dimensions are supplied
            // by whoever wrote the file, so this product is attacker-influenced on both factors;
            // evaluating it in 32-bit arithmetic is how a ceiling gets defeated by overflow instead
            // of by being exceeded.
            //
            // Math.Ceiling per axis, which also absorbs the single-precision geometry: SizeF is
            // float, so a 14400-point page scaled by 150/72 arrives fractionally above 30000 rather
            // than exactly on it. The estimate therefore rounds up. That is the correct direction
            // for a bound - it may refuse a page a hair under the ceiling, and can never admit one
            // over it.
            double pixels = Math.Ceiling(page.Width * scale) * Math.Ceiling(page.Height * scale);

            if (!double.IsFinite(pixels) || pixels > _renderOptions.MaxPagePixels)
            {
                throw Refuse(
                    DocumentNotIngestibleReason.TooLarge,
                    documentPath,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"logical page {index + TextLayer.FirstLogicalPageNumber} declares {page.Width:F0} x {page.Height:F0} points, which at {_renderOptions.Dpi} DPI rasterises to {pixels / 1_000_000:F1} megapixels, above the configured ceiling of {_renderOptions.MaxPageMegapixels}"),
                    innerException: null);
            }

            // The aggregate budget, accumulated in the same pass. The two per-page ceilings above
            // MULTIPLY and their product is unbounded: 500 pages of 69.5 MP each satisfies both and
            // asks for 34,800 megapixels, which is a measured 23.8 minutes of rendering requested by
            // a 102 KB file. Megapixels stand in for time here because PDFium's measured throughput
            // is nearly flat across geometry (22-24 MP/s over a thirtyfold range), so this bounds
            // wall-clock work without knowing anything about the host.
            //
            // Checked inside the loop rather than after it, so an enormous document is refused as
            // soon as its running total crosses the line rather than after every page has been
            // measured. Still before anything is drawn.
            totalPixels += pixels;

            if (totalPixels > _renderOptions.MaxTotalPixels)
            {
                throw Refuse(
                    DocumentNotIngestibleReason.TooLarge,
                    documentPath,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the document's first {index + 1} of {pageSizes.Count} pages already rasterise to {totalPixels / 1_000_000:F1} megapixels at {_renderOptions.Dpi} DPI, above the configured total ceiling of {_renderOptions.MaxTotalMegapixels}"),
                    innerException: null);
            }
        }

        RenderOptions options = BuildRenderOptions();

        List<PageImage> images = [];

        try
        {
            // ToImagesAsync opens the document ONCE and yields its pages, and it takes a real
            // cancellation token. The per-page SavePng overload this replaced re-parsed the whole
            // file per page - N parses for an N-page document, on input an outside party controls.
            //
            // The library's own caveat, worth repeating rather than discovering: cancellation is
            // checked between pages, not inside one. A single page's render is not interruptible,
            // which is a further reason the megapixel ceiling above matters - it bounds the longest
            // uninterruptible unit of work this pipeline can be made to perform.
            await foreach (SKBitmap bitmap in Conversion
                .ToImagesAsync(pdfStream: content, leaveOpen: true, password: null, options: options, cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                using (bitmap)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // A null bitmap where one was promised is the native boundary failing, not the
                    // document being malformed - PDFium reports a malformed document as a typed
                    // PdfException, which is handled separately below.
                    if (bitmap is null)
                    {
                        throw Fault(documentPath, $"the rasteriser yielded no bitmap for logical page {images.Count + TextLayer.FirstLogicalPageNumber}", innerException: null);
                    }

                    using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, quality: 100)
                        ?? throw Fault(documentPath, $"PNG encoding produced nothing for logical page {images.Count + TextLayer.FirstLogicalPageNumber}", innerException: null);

                    byte[] bytes = encoded.ToArray();

                    if (bytes.Length == 0)
                    {
                        throw Fault(documentPath, $"PNG encoding produced an empty buffer for logical page {images.Count + TextLayer.FirstLogicalPageNumber}", innerException: null);
                    }

                    images.Add(new PageImage(images.Count + TextLayer.FirstLogicalPageNumber, PageImageMediaType, bytes));
                }
            }
        }
        catch (PdfException error) when (error is not PdfUnknownException)
        {
            // The document's own fault, and PDFtoImage names it: PdfInvalidFormatException for a
            // corrupt or non-PDF file, PdfPasswordProtectedException and
            // PdfUnsupportedSecuritySchemeException for one this pipeline cannot open,
            // PdfCannotOpenFileException, PdfPageNotFoundException. All deterministic in the bytes,
            // so all a refusal that routes to review rather than to a retry.
            //
            // THE EXCLUSION IS LOAD-BEARING, and its absence was a real defect rather than a
            // stylistic one. The taxonomy WAS probed by reflection before this code was written, and
            // the probe was right: every one of PDFtoImage's seven exception types derives from
            // PdfException, PdfUnknownException included. What went wrong is that the prose acted on
            // the measurement and the code did not - an earlier revision of this comment claimed
            // "PdfUnknownException is deliberately NOT in this arm - see the filter below" while no
            // such filter existed, so a bare `catch (PdfException)` swallowed it here and the fault
            // arm's documentation described a shape it could never receive.
            //
            // Why it matters that PdfUnknownException goes the other way: it is what the wrapper
            // raises when PDFium returns an error code it cannot name. That is a native-boundary
            // event - the observable end of the same spectrum as a memory-safety defect - and it
            // belongs on the Error/1018 line an operator alerts on, not filed at Warning/1017 as
            // "the supplier sent a bad document". Being caught here made the one native signal this
            // class can actually observe indistinguishable from a corrupt PDF.
            throw Refuse(DocumentNotIngestibleReason.Unreadable, documentPath, "the PDF renderer could not read the document", error);
        }
        catch (Exception error) when (error is not PageRenderFaultException
            and not DocumentNotIngestibleException
            and not OperationCanceledException
            and not IOException
            and not OutOfMemoryException)
        {
            // Everything the boundary can produce that is NOT a named statement about the document.
            // An earlier revision had this arm the other way round - unknown exceptions were filed
            // as "document unreadable" - and that was wrong in the direction that hides problems.
            // The shapes that actually arrive here are:
            //
            //   PdfUnknownException     - PDFium returned an error code the wrapper cannot name. It
            //                             reaches this arm only because the refusal arm above
            //                             excludes it explicitly: it derives from PdfException like
            //                             every other type in that hierarchy (measured by
            //                             reflection, and pinned by a test so a package bump breaks
            //                             the build rather than the alert rule).
            //   NullReferenceException  - a Skia or PDFium handle came back null where the managed
            //                             wrapper assumed an object; the usual symptom of a failed
            //                             native allocation, because Skia signals that by returning
            //                             a null pointer rather than by throwing.
            //   SKException             - SkiaSharp's own failure type.
            //   anything else           - by definition unanticipated, which is the strongest reason
            //                             of all to raise it as a renderer fault rather than quietly
            //                             recording it as a bad document.
            //
            // The three exclusions above are the same ones the ingest ports make and for the same
            // reasons: an IOException is infrastructure failing rather than a statement about the
            // document; an OutOfMemoryException is a resource-exhaustion signal that must reach
            // whoever watches for attacks; a cancellation is the caller's own decision.
            //
            // Note what this arm does NOT claim to catch: an access violation inside PDFium
            // terminates the process, and no filter here observes it. See the class remarks.
            throw Fault(documentPath, "an unexpected failure crossed back out of the native rasteriser", error);
        }

        // The claim and the delivery, reconciled, exactly as the text extractor does it. Every page
        // number above is derived from position in this loop, so a render that stopped early would
        // produce an image set that is short, internally consistent and wrong - and the profile
        // would present a truncated document while reporting full visual fidelity for it.
        if (images.Count != pageSizes.Count)
        {
            throw Fault(
                documentPath,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the document declares {pageSizes.Count} pages but the rasteriser yielded {images.Count}"),
                innerException: null);
        }

        return images;
    }

    /// <summary>Builds the render settings, narrowed from the library's defaults to the one knob this pipeline owns.</summary>
    /// <remarks>
    /// Written as a <c>with</c> on a default instance rather than a positional construction on
    /// purpose: <c>RenderOptions</c>' primary constructor is positional, so naming <c>Dpi</c> by
    /// position would silently move if the library reordered its parameters. <c>UseTiling</c> is
    /// left off deliberately - see the class remarks.
    /// </remarks>
    private RenderOptions BuildRenderOptions() => new RenderOptions() with { Dpi = _renderOptions.Dpi };

    /// <summary>Reads every page's declared geometry, so the ceilings can be applied before anything is drawn.</summary>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private IList<SizeF> ReadPageGeometry(string documentPath, Stream content)
    {
        try
        {
            return Conversion.GetPageSizes(pdfStream: content, leaveOpen: true, password: null);
        }
        catch (PdfException error) when (error is not PdfUnknownException)
        {
            // Same split as the render loop, and it has to be repeated because the geometry read is
            // a separate crossing of the same native boundary: PDFium can fail to name an error code
            // while answering "how big are the pages" just as easily as while drawing them.
            throw Refuse(DocumentNotIngestibleReason.Unreadable, documentPath, "the PDF renderer could not read the document's page table", error);
        }
        catch (Exception error) when (error is not DocumentNotIngestibleException
            and not OperationCanceledException
            and not IOException
            and not OutOfMemoryException)
        {
            throw Fault(documentPath, "an unexpected failure crossed back out of the native rasteriser while reading the page table", error);
        }
    }

    /// <summary>Opens the document, letting <see cref="FileNotFoundException"/> and <see cref="IOException"/> reach the caller as the port promises.</summary>
    private static FileStream OpenForReading(string documentPath)
    {
        // RandomAccess for the reason given on the text extractor: a PDF reader seeks to the
        // trailer and cross-reference table before it reads any page, so a sequential-scan hint
        // would describe behaviour this file will not exhibit.
        FileStreamOptions readOptions = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.RandomAccess,
        };

        return new FileStream(documentPath, readOptions);
    }

    /// <summary>Refuses the document, emitting the structured event first.</summary>
    private DocumentNotIngestibleException Refuse(
        DocumentNotIngestibleReason reason,
        string documentPath,
        string detail,
        Exception? innerException)
    {
        LogRenderRefused(
            _logger,
            reason,
            detail,
            PathBoundary.ForLog(documentPath),
            innerException?.GetType().FullName ?? "none");

        return new DocumentNotIngestibleException(reason, documentPath, byteLength: null, innerException);
    }

    /// <summary>Raises a renderer fault, emitting the structured event first.</summary>
    private PageRenderFaultException Fault(string documentPath, string detail, Exception? innerException)
    {
        LogRenderFaulted(
            _logger,
            detail,
            PathBoundary.ForLog(documentPath),
            innerException?.GetType().FullName ?? "none");

        return new PageRenderFaultException(documentPath, detail, innerException);
    }

    [LoggerMessage(
        EventId = CbixEventIds.PageRenderRefused,
        Level = LogLevel.Warning,
        Message = "Page rasterisation refused a document: {Reason} - {Detail}. Document='{DocumentPath}', failure={FailureType}.")]
    private static partial void LogRenderRefused(
        ILogger logger,
        DocumentNotIngestibleReason reason,
        string detail,
        string documentPath,
        string failureType);

    /// <summary>
    /// Error rather than Warning, and separate from the refusal event above. A refusal is the gate
    /// working; this is the renderer failing, on untrusted input, inside native code whose defects
    /// the dependency audit cannot see. It is the line an operator should have an alert on.
    /// </summary>
    [LoggerMessage(
        EventId = CbixEventIds.PageRenderFaulted,
        Level = LogLevel.Error,
        Message = "Page rasteriser faulted: {Detail}. Document='{DocumentPath}', failure={FailureType}.")]
    private static partial void LogRenderFaulted(
        ILogger logger,
        string detail,
        string documentPath,
        string failureType);
}
