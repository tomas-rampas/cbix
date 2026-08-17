using System.Globalization;
using System.Text;

using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.Logging;

using PDFtoImage.Exceptions;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// Probes the PDFium renderer against the real DE specimen, read in place, plus the damaged and
/// oversized inputs that decide what escapes this type (story S01-06).
/// <para>
/// Nothing here is stubbed, and that is the point: this is the only place in the suite where the
/// native library is actually loaded and asked to draw a page. If PDFium cannot load on the host -
/// the case that matters for <c>ubuntu-latest</c> CI - these tests are what says so, rather than a
/// container failing at deploy time.
/// </para>
/// </summary>
public sealed class PdfiumPageImageRendererTests : IDisposable
{
    /// <summary>Page count of the DE specimen, measured from the file rather than assumed.</summary>
    private const int DeSpecimenPageCount = 2;

    /// <summary>
    /// Event ids the renderer emits, pinned so a renumbering breaks a test rather than an alert
    /// rule. They are the log's stable contract - message text is prose and may be reworded, but an
    /// id is what a SIEM keys on. Pinned here for the same reason the text extractor pins its own.
    /// </summary>
    private const int RenderRefusedEventId = 1017;

    /// <inheritdoc cref="RenderRefusedEventId" />
    private const int RenderFaultedEventId = 1018;

    private const string DeSpecimenFileName = "Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf";

    /// <summary>The eight-byte PNG signature (RFC 2083 §3.1).</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("cbix-render-");
    private readonly CapturingLogger _logger = new();

    public void Dispose() => _workspace.Delete(recursive: true);

    [Fact]
    public async Task RenderAsync_DeSpecimen_ProducesOnePngPerPageInOrder()
    {
        IReadOnlyList<PageImage> images = await Renderer().RenderAsync(SpecimenReference());

        Assert.Equal(DeSpecimenPageCount, images.Count);

        for (int index = 0; index < images.Count; index++)
        {
            PageImage image = images[index];

            Assert.Equal(index + TextLayer.FirstLogicalPageNumber, image.LogicalPageNumber);
            Assert.Equal("image/png", image.MediaType);
            Assert.True(image.Content.Span.StartsWith(PngSignature), $"Page {image.LogicalPageNumber} is not a PNG.");

            // Real pixels, not a blank canvas. A page of dense text and a table compresses to far
            // more than this even at 150 DPI; the floor is what would catch a render that produced
            // a valid but empty image, which is otherwise indistinguishable from success.
            Assert.True(image.Content.Length > 8192, $"Page {image.LogicalPageNumber} rendered to only {image.Content.Length} bytes.");
        }

        // The two pages are not the same picture. Cheap, and it rules out the failure mode where a
        // renderer returns page 1 for every index - which would present the matrix page as the cover
        // page and pass every other assertion here.
        Assert.NotEqual(images[0].Content.Length, images[1].Content.Length);
    }

    [Fact]
    public async Task RenderAsync_AtAHigherDensity_ProducesLargerImages()
    {
        // Proves the DPI setting reaches the rasteriser rather than being carried and ignored. It is
        // the one knob this pipeline has for matrix legibility, so "configurable" has to mean it
        // changes the pixels.
        DocumentReference document = SpecimenReference();

        IReadOnlyList<PageImage> standard = await Renderer(PageImageRenderOptions.DefaultDpi).RenderAsync(document);
        IReadOnlyList<PageImage> dense = await Renderer(PageImageRenderOptions.DefaultDpi * 2).RenderAsync(document);

        Assert.True(
            dense[0].Content.Length > standard[0].Content.Length,
            $"Doubling the DPI produced {dense[0].Content.Length} bytes against {standard[0].Content.Length}.");
    }

    [Fact]
    public async Task RenderAsync_NullDocument_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => Renderer().RenderAsync(null!));

    [Fact]
    public async Task RenderAsync_MissingFile_ThrowsFileNotFound()
    {
        // Propagated rather than rewritten as a refusal. Normally unreachable - ingest read this
        // file minutes ago - so reaching it means the ingest share changed under a live run, which
        // is a deployment fault rather than a statement about the document.
        DocumentReference document = new(
            $"sha256:{new string('c', 64)}",
            new Uri(Path.Combine(_workspace.FullName, "absent.pdf")),
            "absent.pdf");

        await Assert.ThrowsAsync<FileNotFoundException>(() => Renderer().RenderAsync(document));
    }

    [Fact]
    public async Task RenderAsync_NotAPdf_IsRefusedAsUnreadable()
    {
        // The native decode failure is mapped into the shared ingest refusal family rather than
        // escaping as whatever PDFium's wrapper happens to throw, so a caller handles one family and
        // swapping the rasteriser is not a breaking change for everyone who catches it.
        DocumentReference document = WriteFile("not-a-pdf.pdf", Encoding.ASCII.GetBytes("this is not a PDF at all, not even close"));

        DocumentNotIngestibleException error = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => Renderer().RenderAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.Unreadable, error.Reason);
    }

    [Fact]
    public async Task RenderAsync_TruncatedPdf_IsRefusedAsUnreadable()
    {
        byte[] whole = await File.ReadAllBytesAsync(SpecimenPath());
        DocumentReference document = WriteFile("truncated.pdf", whole[..(whole.Length / 2)]);

        DocumentNotIngestibleException error = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => Renderer().RenderAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.Unreadable, error.Reason);
    }

    [Fact]
    public async Task RenderAsync_PastTheConfiguredSizeBound_IsRefused()
    {
        // The ingest gate's own bound, re-applied to a handle the gate never saw. Shared as one
        // options object with the text extractor precisely so the two cannot drift apart.
        DocumentIngestOptions options = new(_workspace.FullName, maxDocumentBytes: 1024);
        PdfiumPageImageRenderer renderer = new(options, new PageImageRenderOptions(), _logger);

        byte[] whole = await File.ReadAllBytesAsync(SpecimenPath());
        DocumentReference document = WriteFile("oversized.pdf", whole);

        DocumentNotIngestibleException error = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => renderer.RenderAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.TooLarge, error.Reason);
    }

    [Fact]
    public async Task RenderAsync_PageGeometryBomb_IsRefusedBeforeAnythingIsRasterised()
    {
        // THE reason the megapixel ceiling exists, exercised against the real renderer.
        //
        // This file is a few hundred bytes and entirely legal: one page with a 14400 x 14400 point
        // MediaBox - 200 x 200 inches, exactly the maximum the PDF format permits. At the DEFAULT
        // DPI it rasterises to 30000 x 30000 pixels: 900 megapixels, 3.35 GB at 4 bytes per pixel.
        // Nothing upstream catches it, because as a FILE it is tiny; MaxDocumentBytes sees a few
        // hundred bytes and waves it through. The amplification is roughly eight million to one, and
        // the party that supplies the document chooses it.
        DocumentReference document = WriteFile("geometry-bomb.pdf", BuildPdf(widthPoints: 14400, heightPoints: 14400, pages: 1));

        DocumentNotIngestibleException error = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => Renderer().RenderAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.TooLarge, error.Reason);

        // Refused BEFORE rendering, which is the property that matters - a ceiling enforced after
        // the allocation would be a ceiling that has already lost. The structured event names the
        // page and the arithmetic, so an operator sees why rather than only that.
        (LogLevel level, int eventId, string message) = Assert.Single(_logger.Entries);
        Assert.Equal(RenderRefusedEventId, eventId);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("14400 x 14400 points", message, StringComparison.Ordinal);
        Assert.Contains("above the configured ceiling of 80", message, StringComparison.Ordinal);

        // "900.1", not "900.0", and the extra tenth is worth pinning rather than papering over.
        // Page geometry comes back as System.Drawing.SizeF - single precision - so 14400 points
        // scaled by 150/72 lands a hair above 30000 rather than exactly on it, and the per-axis
        // Math.Ceiling turns that into 30001 pixels. The estimate therefore rounds UP, which is the
        // safe direction for a bound: it can refuse a page a fraction under the ceiling, never admit
        // one over it.
        Assert.Contains("900.1 megapixels", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_PageGeometryWithinTheCeiling_IsStillRendered()
    {
        // Guards the guard. A ceiling that refused ordinary documents would pass the test above
        // while breaking the pipeline, so this pins the other side of the boundary: A3 at the
        // default DPI is nowhere near the ceiling and must render normally.
        DocumentReference document = WriteFile("a3.pdf", BuildPdf(widthPoints: 842, heightPoints: 1191, pages: 2));

        IReadOnlyList<PageImage> images = await Renderer().RenderAsync(document);

        Assert.Equal(2, images.Count);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task RenderAsync_PastTheAggregateBudget_IsRefusedBeforeAnythingIsRasterised()
    {
        // The gap the two per-page ceilings leave open, because they MULTIPLY. Every page here is
        // A3-sized - comfortably under the per-page megapixel ceiling - and the page count is under
        // its ceiling too, so both existing gates wave it through while the total is enormous.
        //
        // Measured at production scale: 500 such pages at 600 DPI is 34,800 megapixels and 23.8
        // minutes of rendering, from a 102 KB file. This probe uses a small page count and a small
        // budget to assert the same gate cheaply.
        DocumentReference document = WriteFile("aggregate-bomb.pdf", BuildPdf(widthPoints: 842, heightPoints: 1191, pages: 20));

        PdfiumPageImageRenderer renderer = new(
            new DocumentIngestOptions(Path.GetTempPath(), DocumentIngestOptions.ClaudeFilesApiLimitBytes),
            new PageImageRenderOptions(maxTotalMegapixels: 10),
            _logger);

        DocumentNotIngestibleException error = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => renderer.RenderAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.TooLarge, error.Reason);

        (LogLevel level, int eventId, string message) = Assert.Single(_logger.Entries);
        Assert.Equal(RenderRefusedEventId, eventId);
        Assert.Contains("above the configured total ceiling of 10", message, StringComparison.Ordinal);

        // Refused partway through the geometry walk, not after measuring all 20 pages: the running
        // total crosses the line and the document is rejected there.
        Assert.Contains("of 20 pages already rasterise", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_WithinTheAggregateBudget_IsStillRendered()
    {
        // Guards the aggregate gate against being set so tight it refuses ordinary work: a real
        // country manual is a handful of A4 pages and must sail through the default budget.
        DocumentReference document = WriteFile("ordinary.pdf", BuildPdf(widthPoints: 595, heightPoints: 842, pages: 6));

        IReadOnlyList<PageImage> images = await Renderer().RenderAsync(document);

        Assert.Equal(6, images.Count);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task RenderAsync_PastThePageCountCeiling_IsRefusedBeforeAnythingIsRasterised()
    {
        // The second document-controlled multiplier. Page count is free to declare - a 1.05 MB file
        // declaring 5000 pages was measured - and it multiplies every per-page cost behind it.
        DocumentReference document = WriteFile("many-pages.pdf", BuildPdf(widthPoints: 595, heightPoints: 842, pages: 12));

        PdfiumPageImageRenderer renderer = new(
            new DocumentIngestOptions(_workspace.FullName, DocumentIngestOptions.ClaudeFilesApiLimitBytes),
            new PageImageRenderOptions(maxPageCount: 10),
            _logger);

        DocumentNotIngestibleException error = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => renderer.RenderAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.TooLarge, error.Reason);

        (LogLevel level, int eventId, string message) = Assert.Single(_logger.Entries);
        Assert.Equal(RenderRefusedEventId, eventId);
        Assert.Contains("declares 12 pages, above the configured ceiling of 10", message, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfUnknownException_DerivesFromPdfException_WhichIsWhyTheRefusalArmMustExcludeIt()
    {
        // Pins the assumption the whole refusal/fault split rests on, so that a PDFtoImage bump
        // which reparents these types breaks the BUILD rather than silently re-routing native-
        // boundary faults into the Warning-level refusal channel - a regression that would be
        // invisible until someone wondered why the renderer alert never fires.
        //
        // This inheritance is exactly what makes the `when (error is not PdfUnknownException)`
        // filter load-bearing: without it, `catch (PdfException)` swallows the unknown-error case.
        // An earlier revision measured this correctly by reflection and then shipped code that
        // contradicted the measurement, which is why it is now asserted rather than commented.
        Assert.True(
            typeof(PdfException).IsAssignableFrom(typeof(PdfUnknownException)),
            "PdfUnknownException no longer derives from PdfException; revisit the catch filters in PdfiumPageImageRenderer.");

        // And the siblings that legitimately belong in the refusal arm.
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(PdfInvalidFormatException)));
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(PdfPasswordProtectedException)));
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(PdfCannotOpenFileException)));

        // The filter's own logic, evaluated the way the catch clause evaluates it: against a
        // PdfException-typed reference, because that is what the runtime hands a catch filter. The
        // static type has to be the base for this to mean anything - written against the derived
        // types the compiler decides it outright (CS8121), which proves the point but does not test
        // the routing.
        PdfException unknown = new PdfUnknownException();
        PdfException malformed = new PdfInvalidFormatException();

        Assert.False(unknown is not PdfUnknownException, "The refusal arm's filter would admit PdfUnknownException.");
        Assert.True(malformed is not PdfUnknownException, "The refusal arm's filter would reject an ordinary malformed-PDF exception.");
    }

    [Fact]
    public async Task RenderAsync_ARefusedDocument_EmitsTheRefusalEventNotTheFaultEvent()
    {
        // The two events are different alert lines. A refusal is the gate working (Warning); a fault
        // is the renderer failing on untrusted input (Error). Filing one as the other either buries
        // a native fault or pages someone about a bad supplier file.
        DocumentReference document = WriteFile("not-a-pdf-2.pdf", Encoding.ASCII.GetBytes("still not a PDF"));

        await Assert.ThrowsAsync<DocumentNotIngestibleException>(() => Renderer().RenderAsync(document));

        Assert.All(_logger.Entries, entry => Assert.Equal(RenderRefusedEventId, entry.EventId));
        Assert.DoesNotContain(_logger.Entries, entry => entry.EventId == RenderFaultedEventId);
    }

    [Fact]
    public async Task RenderAsync_WithACancelledToken_Throws()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Renderer().RenderAsync(SpecimenReference(), cancellation.Token));
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        DocumentIngestOptions ingest = new(_workspace.FullName, 1024);

        Assert.Throws<ArgumentNullException>(() => new PdfiumPageImageRenderer(null!, new PageImageRenderOptions(), _logger));
        Assert.Throws<ArgumentNullException>(() => new PdfiumPageImageRenderer(ingest, null!, _logger));
        Assert.Throws<ArgumentNullException>(() => new PdfiumPageImageRenderer(ingest, new PageImageRenderOptions(), null!));
    }

    /// <summary>
    /// Builds a minimal but structurally valid PDF with the given page geometry and page count.
    /// </summary>
    /// <remarks>
    /// Hand-built rather than committed as a fixture, and that is the point: the interesting inputs
    /// here are documents whose declared geometry is hostile, and a repository is the wrong place to
    /// keep a file whose entire purpose is to make a renderer allocate 3 GB. Built in the same style
    /// as <c>PdfPigTextLayerExtractorTests.BuildPdf</c>, which exists for the same reason.
    /// </remarks>
    private static byte[] BuildPdf(int widthPoints, int heightPoints, int pages)
    {
        List<string> objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(index => $"{3 + (index * 2)} 0 R"))}] /Count {pages} >>",
        ];

        for (int index = 0; index < pages; index++)
        {
            objects.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {widthPoints} {heightPoints}] /Contents {4 + (index * 2)} 0 R /Resources << >> >>"));
            objects.Add("<< /Length 0 >>\nstream\n\nendstream");
        }

        StringBuilder pdf = new("%PDF-1.7\n");
        List<int> offsets = [];

        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        int crossReferenceStart = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");

        foreach (int offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{crossReferenceStart}\n%%EOF");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private PdfiumPageImageRenderer Renderer(int dpi = PageImageRenderOptions.DefaultDpi) =>
        new(
            // Only MaxDocumentBytes is read by the renderer - containment is the registry's job and
            // is deliberately not re-checked here - so the root is any valid path. These probes
            // render the committed specimen and hand-built hostile documents from a temp workspace,
            // which is why it is not the data directory.
            new DocumentIngestOptions(Path.GetTempPath(), DocumentIngestOptions.ClaudeFilesApiLimitBytes),
            new PageImageRenderOptions(dpi),
            _logger);

    /// <summary>Builds a reference to the specimen committed in the repository, read in place.</summary>
    /// <remarks>
    /// The identity is a stand-in: this type is not the registry and does not derive identities.
    /// </remarks>
    private static DocumentReference SpecimenReference() =>
        new($"sha256:{new string('a', 64)}", new Uri(SpecimenPath()), DeSpecimenFileName);

    private static string SpecimenPath()
    {
        string path = Path.Combine(FindRepositoryRoot(), "data", DeSpecimenFileName);

        Assert.True(File.Exists(path), $"The specimen is missing from the repository at '{path}'.");

        return path;
    }

    /// <summary>Walks up from the test assembly's location to the directory holding <c>Cbix.sln</c>.</summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cbix.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"No directory containing 'Cbix.sln' was found above '{AppContext.BaseDirectory}'.");

        return directory!.FullName;
    }

    private DocumentReference WriteFile(string fileName, byte[] content)
    {
        string path = Path.Combine(_workspace.FullName, fileName);
        File.WriteAllBytes(path, content);

        return new DocumentReference($"sha256:{new string('b', 64)}", new Uri(path), fileName);
    }

    /// <summary>
    /// Captures the structured events the renderer emits, so the refusal and fault paths can be
    /// asserted on their event ids rather than only on the exception they raise.
    /// </summary>
    /// <remarks>
    /// A private nested double per test class is the pattern already used by
    /// <c>PdfPigTextLayerExtractorTests</c> and <c>DocumentIngestServiceTests</c>; each is typed to
    /// the logger's own generic parameter, which is what stops one being silently passed where
    /// another was meant.
    /// </remarks>
    private sealed class CapturingLogger : ILogger<PdfiumPageImageRenderer>
    {
        private readonly List<(LogLevel Level, int EventId, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, int EventId, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _entries.Add((logLevel, eventId.Id, formatter(state, exception)));
        }
    }
}
