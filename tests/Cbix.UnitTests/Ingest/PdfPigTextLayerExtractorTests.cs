using System.Globalization;
using System.Text;

using Cbix.Core.Diagnostics;
using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.Logging;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Probes the PDFPig text layer against the real DE specimen, read in place, plus the damaged-input
/// cases that decide what escapes this type.
/// <para>
/// The verbatim-phrase assertions are the ones that matter most, and they are not decoration: the
/// validator's grounding gate is a string-containment check against exactly this output
/// (design 5.6), so an extraction change that silently normalised a character would turn correct
/// model extractions into gate failures. The phrases below were taken from the extractor's own
/// output rather than typed from the PDF, then checked to appear on one page only.
/// </para>
/// </summary>
public sealed class PdfPigTextLayerExtractorTests : IDisposable
{
    /// <summary>Page count of the DE specimen, measured from the file rather than assumed.</summary>
    private const int DeSpecimenPageCount = 2;

    /// <summary>
    /// Event ids the extractor emits, pinned so a renumbering breaks a test rather than an alert
    /// rule. They are the log's stable contract - message text is prose and may be reworded, but an
    /// id is what a SIEM keys on, so it is the part that must not drift silently.
    /// </summary>
    /// <remarks>
    /// Taken from <see cref="CbixEventIds"/> rather than restated as literals. Restating them is what
    /// let the open-failure event and <c>DocumentIngestService</c>'s Critical credential failure both
    /// sit on 1015: the literal here agreed with the literal there, and neither knew about the other.
    /// The open-failure id is now 1019; this test is what proves the renumbering actually landed on
    /// the emitted event and not only in the registry.
    /// </remarks>
    private const int TextLayerRefusedEventId = CbixEventIds.IngestTextLayerRefused;

    /// <inheritdoc cref="TextLayerRefusedEventId" />
    private const int TextLayerOpenFailedEventId = CbixEventIds.IngestTextLayerOpenFailed;

    private const string DeSpecimenFileName = "Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf";
    private const string ChSpecimenFileName = "Cross_Border_Trading_Legal_Instruction_CH_SPECIMEN.pdf";

    private readonly DirectoryInfo _workspace = Directory.CreateTempSubdirectory("cbix-textlayer-");
    private readonly CapturingLogger _logger = new();

    public void Dispose()
    {
        _workspace.Delete(recursive: true);
    }

    [Fact]
    public async Task ExtractAsync_DeSpecimen_ProducesTextForEveryPage()
    {
        TextLayer layer = await Extractor().ExtractAsync(SpecimenReference(DeSpecimenFileName));

        Assert.Equal(DeSpecimenPageCount, layer.PageCount);
        Assert.Equal(DeSpecimenPageCount, layer.Pages.Count);

        for (int page = TextLayer.FirstLogicalPageNumber; page <= layer.PageCount; page++)
        {
            Assert.True(layer.TryGetPage(page, out string? text), $"Logical page {page} is missing from the text layer.");
            Assert.False(string.IsNullOrWhiteSpace(text), $"Logical page {page} produced no text.");
        }

        // Retrieval by logical page number is the contract the grounding gate uses: it looks on the
        // page the agent named, so a page number that resolved to the wrong page - or to nothing -
        // would silently ground snippets against the wrong text.
        Assert.Same(layer.Pages[0], layer.GetPage(TextLayer.FirstLogicalPageNumber));
        Assert.Same(layer.Pages[^1], layer.GetPage(layer.PageCount));
        Assert.False(layer.TryGetPage(layer.PageCount + 1, out _));
    }

    [Theory]
    // A row of the regulator table, spanning two columns. It also carries a non-ASCII character -
    // U+00FC LATIN SMALL LETTER U WITH DIAERESIS, in the German word for "for" - written as an
    // escape rather than as the character itself, so that neither this file's encoding nor a
    // reformatting pass can quietly change what is being asserted. If extraction ever folded that
    // letter to a plain 'u', grounding would begin rejecting correctly-copied German snippets.
    [InlineData(1, "BaFin - Bundesanstalt f\u00fcr Finanzdienstleistungsaufsicht Conduct and prudential supervision")]
    // A permission-matrix row, cells and superscript condition references included. This is the
    // hard case of the whole document, and the one the Matrix agent's snippets will come from.
    [InlineData(1, "Cash equities - execution & dealing P1 P1 PR2 NP")]
    // A document-control field, the shape DocControl's snippets take.
    [InlineData(1, "Document Reference CBTI-DE-2026-011")]
    // Prose on the second page, with punctuation and a decimal percentage intact.
    [InlineData(2, "Dividend withholding tax of 26.375% (25% plus solidarity surcharge) applies at source")]
    // A numbered footnote, the shape the Conditions agent cites.
    [InlineData(2, "(4) A PRIIPs KID must be available before distribution")]
    public async Task ExtractAsync_DeSpecimen_PreservesKnownPhrasesVerbatimOnTheirOwnPage(int logicalPage, string phrase)
    {
        TextLayer layer = await Extractor().ExtractAsync(SpecimenReference(DeSpecimenFileName));

        Assert.Contains(phrase, layer.GetPage(logicalPage), StringComparison.Ordinal);

        // Page-local, not merely present somewhere. A gate that only asked "is this string anywhere
        // in the document" would accept a snippet attributed to the wrong page, and the page number
        // is half of the provenance an auditor is given.
        for (int page = TextLayer.FirstLogicalPageNumber; page <= layer.PageCount; page++)
        {
            if (page != logicalPage)
            {
                Assert.DoesNotContain(phrase, layer.GetPage(page), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task ExtractAsync_ChSpecimen_AlsoProducesTextForEveryPage()
    {
        // The second specimen is here because the DE one alone cannot show that anything general is
        // happening - the two documents use different categorisation schemes and different layouts.
        TextLayer layer = await Extractor().ExtractAsync(SpecimenReference(ChSpecimenFileName));

        Assert.Equal(2, layer.PageCount);
        Assert.All(layer.Pages, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Contains("CBTI-CH-", layer.GetPage(1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_CarriesTheDocumentIdentityOntoTheTextLayer()
    {
        DocumentReference document = SpecimenReference(DeSpecimenFileName);

        TextLayer layer = await Extractor().ExtractAsync(document);

        // The text layer travels in run state and is what a published value's snippet is checked
        // against; if it could not say which document it came from, "grounded" would be an
        // assumption rather than a fact.
        Assert.Equal(document.DocumentId, layer.DocumentId);
    }

    [Theory]
    [InlineData("not-a-pdf.pdf", "this is not a PDF at all, not even close")]
    [InlineData("empty.pdf", "")]
    public async Task ExtractAsync_BytesThatAreNotAReadablePdf_RefuseWithTheIngestRefusalFamily(string fileName, string content)
    {
        // Both cases matter, and for different reasons. Garbage bytes are what the PDF library's own
        // format exception covers. A zero-length file is not: it was measured to raise a bare
        // ArgumentOutOfRangeException about a negative offset from inside the parser, so catching
        // only the library's exception type would let an unexplained argument error out of ingest.
        DocumentReference document = WriteFile(fileName, Encoding.ASCII.GetBytes(content));

        DocumentNotIngestibleException refusal =
            await Assert.ThrowsAsync<DocumentNotIngestibleException>(() => Extractor().ExtractAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.Unreadable, refusal.Reason);
        Assert.Equal(document.Location.LocalPath, refusal.DocumentPath);

        // Reason-derived and path-free, like every other ingest refusal: the message travels into
        // logs and the path in it is input somebody else chose.
        Assert.DoesNotContain(refusal.DocumentPath, refusal.Message, StringComparison.Ordinal);

        // The parser's own description survives for diagnosis - but as Exception, so no caller can
        // bind a catch clause to a PDF-library type and make replacing the parser a breaking change.
        Assert.NotNull(refusal.InnerException);
    }

    [Fact]
    public async Task ExtractAsync_TruncatedPdf_RefusesRatherThanReturningAPartialTextLayer()
    {
        // A half-copied file off a share is the realistic damage case, and the dangerous failure
        // would be a text layer covering the pages that survived: grounding would then reject every
        // correct snippet from the missing pages as invented.
        byte[] whole = await File.ReadAllBytesAsync(SpecimenPath(DeSpecimenFileName));
        DocumentReference document = WriteFile("truncated.pdf", whole[..(whole.Length / 2)]);

        DocumentNotIngestibleException refusal =
            await Assert.ThrowsAsync<DocumentNotIngestibleException>(() => Extractor().ExtractAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.Unreadable, refusal.Reason);
    }

    [Fact]
    public async Task ExtractAsync_UnreadableDocument_EmitsAStructuredErrorEventWithNoParserText()
    {
        // The same garbage bytes as the theory above, and long enough to reach the parser's header
        // check: a four-byte file is refused earlier, by offset arithmetic, and would not exercise
        // the case this test is about - the library's own diagnostic being kept out of the log.
        DocumentReference document = WriteFile("not-a-pdf.pdf", Encoding.ASCII.GetBytes("this is not a PDF at all, not even close"));

        await Assert.ThrowsAsync<DocumentNotIngestibleException>(() => Extractor().ExtractAsync(document));

        (LogLevel Level, int EventId, string Message) entry = Assert.Single(_logger.Entries);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(TextLayerRefusedEventId, entry.EventId);
        Assert.Contains(document.Location.LocalPath, entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DocumentNotIngestibleReason.Unreadable), entry.Message, StringComparison.Ordinal);

        // The failure is recorded by type. A parser's message describes the file's internal
        // structure and is the one string on this path that nothing has sanitised.
        Assert.Contains("UglyToad.PdfPig.Core.PdfDocumentFormatException", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("version header", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_DocumentThatRenumbersItsOwnPages_IsRefusedRatherThanGuessedAt()
    {
        // The attack this closes is silent and total. A /PageLabels number tree lets a document print
        // page 3 as "iii" or restart the count at 1; an agent told to report "logical page numbers as
        // shown in a PDF viewer" reports the printed label, while this text layer is keyed by
        // physical index - so every provenance record for the document cites a page that indexes
        // different text, and nothing downstream can tell.
        //
        // Measured on the crafted document below: PdfPig reports Page.Number as 1, 2, 3 regardless,
        // which is why the page-order check this replaced could never have caught it.
        DocumentReference document = WriteFile("relabelled.pdf", BuildPdf(withPageLabels: true));

        DocumentNotIngestibleException refusal =
            await Assert.ThrowsAsync<DocumentNotIngestibleException>(() => Extractor().ExtractAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.UnsupportedPageNumbering, refusal.Reason);

        // Not a damaged document, and the refusal must not say it is: this routes to human review as
        // an unknown layout (design 5.3), and an operator's response is to confirm the numbering, not
        // to chase the supplier for a better file.
        Assert.Null(refusal.InnerException);
        Assert.DoesNotContain("could not be read", refusal.Message, StringComparison.Ordinal);

        (LogLevel Level, int EventId, string Message) entry = Assert.Single(_logger.Entries);
        Assert.Equal(TextLayerRefusedEventId, entry.EventId);
        Assert.Contains(nameof(DocumentNotIngestibleReason.UnsupportedPageNumbering), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_OtherwiseIdenticalDocumentWithoutPageLabels_IsAccepted()
    {
        // The control for the test above. Without it, that refusal could be firing on anything about
        // the crafted document - its hand-built xref, its lack of metadata - rather than on the one
        // key that is supposed to trigger it.
        DocumentReference document = WriteFile("plain.pdf", BuildPdf(withPageLabels: false));

        TextLayer layer = await Extractor().ExtractAsync(document);

        Assert.Equal(3, layer.PageCount);
        Assert.Contains("Page marker 1", layer.GetPage(1), StringComparison.Ordinal);
        Assert.Contains("Page marker 3", layer.GetPage(3), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DeSpecimenFileName)]
    [InlineData(ChSpecimenFileName)]
    public async Task ExtractAsync_TheSpecimens_CarryNoPageLabelsAndAreAccepted(string fileName)
    {
        // Measured: neither specimen carries /PageLabels, so the refusal above costs nothing in
        // scope. Pinned as a test because the cost of that guardrail is only acceptable while this
        // stays true - a specimen that acquired page labels would take the whole pipeline offline for
        // that document, and it should be this test that says so rather than a failing pilot run.
        TextLayer layer = await Extractor().ExtractAsync(SpecimenReference(fileName));

        Assert.Equal(2, layer.PageCount);
    }

    [Fact]
    public async Task ExtractAsync_ReopenedFileLargerThanTheConfiguredCap_IsRefusedAsTooLarge()
    {
        // The gate counted its bound while hashing; this handle is a second, later open of the same
        // path, and until this check existed it was subject to no bound at all. The window is small
        // but the consequence is not bounded by anything else: a file swapped for an enormous one
        // between the hash and the parse was read to completion.
        DocumentReference document = WriteFile("plain.pdf", BuildPdf(withPageLabels: false));
        long actualLength = new FileInfo(document.Location.LocalPath).Length;

        DocumentNotIngestibleException refusal = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => Extractor(maxDocumentBytes: actualLength - 1).ExtractAsync(document));

        Assert.Equal(DocumentNotIngestibleReason.TooLarge, refusal.Reason);

        // And the same document is accepted when the cap admits it, so the refusal is the cap
        // talking rather than anything about the file.
        TextLayer layer = await Extractor(maxDocumentBytes: actualLength).ExtractAsync(document);
        Assert.Equal(3, layer.PageCount);
    }

    [Fact]
    public async Task ExtractAsync_FileThatCannotBeOpened_PropagatesTheIoFailureAndLogsIt()
    {
        // Two findings in one probe.
        //
        // The IOException must reach the caller as itself. Rewriting an infrastructure fault as an
        // Unreadable refusal would tell an operator that a supplier sent a bad file when in fact the
        // share went away - and because a refusal is terminal for the run while the document stays
        // registered, a transient outage would leave that document permanently without a text layer.
        //
        // And the open must be logged. It sits outside the parse, so nothing there treats it as a
        // refusal, and its own message carries the raw path - the same reasoning that gives
        // DocumentIngestService's read failures an event of their own.
        DocumentReference document = WriteFile("locked.pdf", BuildPdf(withPageLabels: false));

        using FileStream exclusive = new(
            document.Location.LocalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

        IOException failure = await Assert.ThrowsAnyAsync<IOException>(() => Extractor().ExtractAsync(document));

        Assert.IsNotType<DocumentNotIngestibleException>(failure);

        (LogLevel Level, int EventId, string Message) entry = Assert.Single(_logger.Entries);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(TextLayerOpenFailedEventId, entry.EventId);
        Assert.Contains("IOException", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractAsync_MissingFile_SurfacesTheFileSystemFailureUnchanged()
    {
        // Deliberately not folded into the Unreadable refusal: "the share is gone" and "this file is
        // damaged" call for different operational responses, and rewriting the first as the second
        // would send an operator to look at the document.
        DocumentReference document = new(
            "sha256:1111111111111111111111111111111111111111111111111111111111111111",
            new Uri(Path.Combine(_workspace.FullName, "never-written.pdf")),
            "never-written.pdf");

        await Assert.ThrowsAsync<FileNotFoundException>(() => Extractor().ExtractAsync(document));
    }

    [Fact]
    public async Task ExtractAsync_CancelledBeforeItStarts_DoesNotOpenTheFile()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Extractor().ExtractAsync(SpecimenReference(DeSpecimenFileName), cancellation.Token));
    }

    [Fact]
    public async Task ExtractAsync_NullDocument_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Extractor().ExtractAsync(null!));
    }

    [Fact]
    public void Constructor_NullCollaborators_Throw()
    {
        DocumentIngestOptions options = new(_workspace.FullName, DocumentIngestOptions.ClaudeFilesApiLimitBytes);

        Assert.Throws<ArgumentNullException>(() => new PdfPigTextLayerExtractor(null!, _logger));
        Assert.Throws<ArgumentNullException>(() => new PdfPigTextLayerExtractor(options, null!));
    }

    private PdfPigTextLayerExtractor Extractor(long? maxDocumentBytes = null) =>
        new(
            new DocumentIngestOptions(
                _workspace.FullName,
                maxDocumentBytes ?? DocumentIngestOptions.ClaudeFilesApiLimitBytes),
            _logger);

    /// <summary>
    /// Builds a minimal three-page PDF, optionally carrying a <c>/PageLabels</c> number tree.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than produced by a builder API, because the point of the document is a
    /// catalogue entry no builder exposes. The labels say "number the first two pages in lowercase
    /// roman, then restart at arabic 1" - so the pages print as i, ii, 1 while their physical indices
    /// remain 1, 2, 3, which is exactly the divergence that makes page-keyed provenance untrustworthy.
    /// Offsets for the cross-reference table are computed from the emitted text rather than written
    /// by hand, so the fixture stays valid if anything above it changes length.
    /// </remarks>
    private static byte[] BuildPdf(bool withPageLabels)
    {
        string labels = withPageLabels
            ? "/PageLabels << /Nums [0 << /S /r >> 2 << /S /D /St 1 >>] >> "
            : string.Empty;

        List<string> objects =
        [
            $"<< /Type /Catalog /Pages 2 0 R {labels}>>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
        ];

        for (int page = 0; page < 3; page++)
        {
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                    + $"/Resources << /Font << /F1 6 0 R >> >> /Contents {7 + page} 0 R >>");
        }

        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (int page = 0; page < 3; page++)
        {
            string stream = $"BT /F1 12 Tf 20 100 Td (Page marker {page + 1}) Tj ET";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
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

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n")
            .Append(CultureInfo.InvariantCulture, $"startxref\n{crossReferenceStart}\n%%EOF");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>Builds a reference to a specimen committed in the repository, read in place.</summary>
    /// <remarks>
    /// The identity is a stand-in: this type is not the registry and does not derive identities, it
    /// carries through whatever the registry put on the reference. What matters for these tests is
    /// that the identity reaching <see cref="TextLayer.DocumentId"/> is the one on the reference.
    /// </remarks>
    private static DocumentReference SpecimenReference(string fileName) =>
        new(
            $"sha256:{new string('a', 64)}",
            new Uri(SpecimenPath(fileName)),
            fileName);

    private static string SpecimenPath(string fileName)
    {
        string path = Path.Combine(FindRepositoryRoot(), "data", fileName);

        Assert.True(File.Exists(path), $"The specimen is missing from the repository at '{path}'.");

        return path;
    }

    private DocumentReference WriteFile(string fileName, byte[] content)
    {
        string path = Path.Combine(_workspace.FullName, fileName);
        File.WriteAllBytes(path, content);

        return new DocumentReference(
            $"sha256:{new string('b', 64)}",
            new Uri(path),
            fileName);
    }

    /// <summary>
    /// Walks up from the test assembly's location to the directory holding <c>Cbix.sln</c>.
    /// </summary>
    /// <remarks>
    /// The specimens are repository data read in place, not build artefacts copied to the output
    /// directory: copying would duplicate a binary fixture per test project and let the copies drift
    /// from the golden set that describes them.
    /// </remarks>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cbix.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"No directory containing 'Cbix.sln' was found above '{AppContext.BaseDirectory}'.");

        return directory!.FullName;
    }

    /// <summary>Captures log entries so the refusal signal can be asserted rather than assumed.</summary>
    private sealed class CapturingLogger : ILogger<PdfPigTextLayerExtractor>
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
