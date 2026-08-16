using System.Globalization;

using Cbix.Core.Documents;

using Microsoft.Extensions.Logging;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Tokens;

namespace Cbix.Core.Ingest;

/// <summary>
/// The PDFPig implementation of <see cref="ITextLayerExtractor"/>: the local text layer named in
/// design 5.1, produced offline at no per-call cost.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the extraction actually does, because the grounding gate's contract depends on it.</b>
/// Text comes from <c>ContentOrderTextExtractor.GetText(page)</c> with PDFPig's default options.
/// That means:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Reading order is content order.</b> Letters are emitted in the order the page's content stream
/// draws them, grouped into words and lines, with a single <c>\n</c> between blocks. It is not a
/// layout analysis: no column detection, no table reconstruction. For these documents that is
/// correct - the specimens draw their tables row by row, so a matrix row arrives as one line, cells
/// separated by spaces (measured: <c>"Cash equities - execution &amp; dealing P1 P1 PR2 NP"</c>).
/// </item>
/// <item>
/// <b>Word spacing is inferred, and this is the whole reason the raw property is not used.</b> A PDF
/// positions glyphs; it need not contain space characters at all. <see cref="Page.Text"/> simply
/// concatenates the letters, which on page 1 of the DE specimen yields
/// <c>"SPECIMEN - SYNTHETIC TEST DATAContoso"</c> and <c>"Instruction - DERef:"</c> - runs of words
/// welded together at every text-show boundary. A snippet an agent copied from the same page would
/// fail containment against that, so grounding would reject correct extractions. The content-order
/// extractor infers the gaps instead (measured on the DE specimen: 2,868 characters for page 1
/// against 2,701 raw, and 3,434 against 3,333 for page 2).
/// </item>
/// <item>
/// <b>Nothing else is changed.</b> No trimming, no case folding, no whitespace collapsing, no
/// Unicode normalisation, no de-hyphenation, no removal of headers, footers or the watermark. Every
/// one of those would be a character the gate cannot match. Non-ASCII survives verbatim - measured:
/// <c>"BaFin - Bundesanstalt für Finanzdienstleistungsaufsicht"</c>.
/// </item>
/// <item>
/// <b>The optional heuristics stay off.</b> <c>SeparateParagraphsWithDoubleNewline</c> would insert
/// characters the page does not have. <c>ReplaceWhitespaceWithSpace</c> would rewrite characters the
/// page does have. <c>NegativeGapAsWhitespace</c> is a table-spacing heuristic that could plausibly
/// help here, and was measured against both specimens: it produced byte-identical output, so
/// enabling it would buy nothing and add a heuristic that changes with a PDFPig upgrade. Turning any
/// of them on is a change to the grounding corpus and needs a green golden-set run behind it.
/// </item>
/// </list>
/// <para>
/// <b>Logical page number equals physical page number, and that is enforced rather than assumed.</b>
/// PDFPig's <see cref="Page.Number"/> is the 1-based physical index, and the specimens number their
/// pages the same way a reader does ("Page 1", "Page 2"), so the two coincide and
/// <see cref="TextLayer.GetPage"/> answers the page an agent names. A PDF may disagree: a
/// <c>/PageLabels</c> number tree renames pages arbitrarily, so physical page 3 can print as "iii"
/// or restart at "1". An agent told to report "logical page numbers as shown in a PDF viewer" would
/// report the printed label, and every provenance record for that document would then cite a page
/// number this text layer indexes differently - silently, and in a way no downstream check could
/// detect. Such a document is therefore <em>refused</em>, not guessed at: it is routed to review as
/// an unknown layout, exactly as design 5.3 requires of a document the pipeline cannot profile.
/// </para>
/// <para>
/// <b>Why a structural check and not a page-order check.</b> An earlier version compared
/// <see cref="Page.Number"/> against the enumeration index and called that the guardrail. It was a
/// tautology: PDFPig assigns that property <em>from</em> the enumeration index, so the comparison
/// can never fail, and a crafted document carrying <c>/PageLabels</c> sailed through it with
/// silently shifted numbering (measured: a three-page document labelled i, ii, 1 yields
/// <c>Page.Number</c> of 1, 2, 3). Interrogating the catalogue is the check that actually looks at
/// what the document claims. Measured against both specimens: neither carries <c>/PageLabels</c>, so
/// the refusal costs nothing in scope. Supporting labelled documents means mapping the number tree
/// onto physical indices and teaching the prompts which numbering to report; that is deliberately
/// not attempted here, because a mapping with no test document behind it is a guess wearing a
/// guardrail's clothes.
/// </para>
/// <para>
/// <b>The file is opened again here, having already been read and hashed by
/// <see cref="DocumentIngestService"/>.</b> That leaves a window in which the bytes parsed are not
/// the bytes hashed. It is the same TOCTOU residual already recorded for the containment stages, and
/// it is bounded by the same deployment control (the ingest root is write-restricted to the ingest
/// service principal). The alternative - holding the ingest read handle open across the registry
/// round trip so one handle serves both - would extend a share lock over a network call to buy
/// protection against a case that control already covers. What the re-open must <em>not</em> do is
/// escape the size bound the gate applied, which is why the cap is re-checked below on the reopened
/// handle: without it, a file swapped for an enormous one inside that window would be parsed to
/// completion.
/// </para>
/// <para>
/// <b>Resource exhaustion: what is bounded, what is not, and what covers the gap.</b> Stated plainly
/// rather than implied, matching the other residuals recorded in this assembly.
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Bounded.</b> Input size, twice - once counted during the ingest read, once re-checked here on
/// the reopened handle against the same configured maximum.
/// </item>
/// <item>
/// <b>Not bounded: decompression.</b> A PDF's content streams are compressed, and PDFPig's flate
/// decoder decompresses into an unbounded <see cref="MemoryStream"/>. A small file can therefore
/// expand to an arbitrarily large allocation - the classic decompression bomb - and the input cap
/// bounds nothing about it. An <see cref="OutOfMemoryException"/> is the observable symptom, which is
/// exactly why it is excluded from the refusal filter below: filed as a data-quality refusal it
/// would be an attack indicator recorded as somebody's malformed paperwork.
/// </item>
/// <item>
/// <b>Not bounded: wall-clock and page count.</b> There is no timeout on the parse and no ceiling on
/// how many pages will be enumerated, so a pathological document can occupy a worker for an
/// arbitrary time.
/// </item>
/// <item>
/// <b>Compensating control, and the deferral.</b> The same one the containment stages rely on: the
/// ingest root is write-restricted to the ingest service principal, so documents arrive from a
/// controlled share rather than from arbitrary submitters, and every refusal here is a structured
/// event that monitors that assumption. A page-count ceiling and a wall-clock budget are deliberately
/// deferred to Sprint 02+, when the workflow gains per-executor timeouts and the eval harness can say
/// what a legitimate document's page count actually is - a limit guessed now would be a limit
/// calibrated against nothing.
/// </item>
/// </list>
/// <para>
/// <b>Synchronous work behind an asynchronous signature.</b> PDFPig has no asynchronous API: parsing
/// is CPU-bound work over synchronous file reads. The method therefore does its work on the calling
/// thread and returns a completed task, rather than wrapping it in <see cref="Task.Run(Action)"/> -
/// that would not make anything asynchronous, it would move the same block onto a pool thread and
/// add a scheduling hop. A host that needs the calling thread back owns that decision; a worker
/// processing one document per run does not. The signature is asynchronous because the port is, and
/// the port is because a future implementation reading from object storage would genuinely need to
/// be.
/// </para>
/// </remarks>
public sealed partial class PdfPigTextLayerExtractor : ITextLayerExtractor
{
    /// <summary>
    /// The catalogue key whose presence means the document numbers its own pages.
    /// </summary>
    /// <remarks>
    /// Built through <see cref="NameToken.Create(string)"/> rather than compared as a string: the
    /// catalogue is a token dictionary, and looking a name up as text would depend on how PDFPig
    /// happens to render tokens today.
    /// </remarks>
    private static readonly NameToken PageLabelsKey = NameToken.Create("PageLabels");

    private readonly DocumentIngestOptions _options;
    private readonly ILogger<PdfPigTextLayerExtractor> _logger;

    /// <summary>Initialises a new <see cref="PdfPigTextLayerExtractor"/>.</summary>
    /// <param name="options">
    /// The ingest configuration, for <see cref="DocumentIngestOptions.MaxDocumentBytes"/>.
    /// <para>
    /// The whole options object is taken rather than the number alone, deliberately. The bound this
    /// applies has to be <em>the same</em> bound the ingest gate counted against, and sharing the
    /// object makes that structural instead of a wiring convention that a composition root could
    /// drift. <see cref="DocumentIngestOptions.IngestRoot"/> is not read here - containment was
    /// settled before a reference existed.
    /// </para>
    /// </param>
    /// <param name="logger">Sink for the structured events emitted when a document cannot be opened or parsed.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public PdfPigTextLayerExtractor(DocumentIngestOptions options, ILogger<PdfPigTextLayerExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Argument validation and every refusal are raised synchronously rather than returned as a
    /// faulted task. Callers await this immediately, so the distinction is invisible to them; it is
    /// stated because the method is not <c>async</c>, and a reader is entitled to know that on
    /// purpose.
    /// </remarks>
    public Task<TextLayer> ExtractAsync(DocumentReference document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        cancellationToken.ThrowIfCancellationRequested();

        string documentPath = document.Location.LocalPath;

        using FileStream content = OpenForReading(documentPath);

        // The bound the gate counted, re-applied to the handle the gate never saw. This one tests the
        // stream's claimed length rather than counting bytes as they arrive, because PDFPig owns the
        // reading from here on and there is nothing to count. That makes it strictly weaker than the
        // gate's counted check - a file that grows during the parse is not caught - and it is still
        // the difference between refusing an enormous file and parsing it to completion.
        if (content.Length > _options.MaxDocumentBytes)
        {
            throw Refuse(
                DocumentNotIngestibleReason.TooLarge,
                documentPath,
                "the reopened document exceeds the configured maximum document size",
                innerException: null);
        }

        List<string> pages = [];
        int declaredPageCount;

        try
        {
            // Declared after the stream so it is disposed before it: PDFPig reads lazily from the
            // stream it was handed, and a document outliving its stream is a document that fails on
            // its next read.
            using PdfDocument pdf = PdfDocument.Open(content);

            if (pdf.Structure.Catalog.CatalogDictionary.TryGet(PageLabelsKey, out _))
            {
                throw Refuse(
                    DocumentNotIngestibleReason.UnsupportedPageNumbering,
                    documentPath,
                    "the document carries a /PageLabels number tree, so its printed page numbers need not match its physical ones",
                    innerException: null);
            }

            declaredPageCount = pdf.NumberOfPages;

            if (declaredPageCount < 1)
            {
                throw Refuse(
                    DocumentNotIngestibleReason.Unreadable,
                    documentPath,
                    "the document reports no pages",
                    innerException: null);
            }

            foreach (Page page in pdf.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();

                pages.Add(ContentOrderTextExtractor.GetText(page));
            }
        }
        catch (Exception error) when (error is not DocumentNotIngestibleException
            and not OperationCanceledException
            and not IOException
            and not OutOfMemoryException)
        {
            // Everything remaining, not PDFPig's format exception alone. Measured against plausible
            // damage: garbage bytes, a truncated file and a smashed trailer all raise
            // PdfDocumentFormatException, but a zero-length input raises a bare
            // ArgumentOutOfRangeException about a negative offset. Catching only the library's own
            // type would let that one out of ingest as an unexplained argument error.
            //
            // The two exclusions above them are the point of this filter, though:
            //
            //   IOException - the share dropped, the handle died, the network went away. That is not
            //   a statement about the document, and rewriting it as Unreadable would tell an operator
            //   that a supplier sent a bad file when in fact the infrastructure failed. It also
            //   breaks ITextLayerExtractor's contract, which promises IOException propagates.
            //
            //   OutOfMemoryException - the observable symptom of the decompression bomb described in
            //   the class remarks. It is a resource-exhaustion signal that belongs in front of
            //   whoever watches for attacks, and Unreadable is a non-alerting data-quality outcome.
            //
            // Both propagate, which also means neither poisons the registry: a transient share
            // failure leaves the run failed rather than leaving a permanently text-less registration
            // behind it.
            throw Refuse(
                DocumentNotIngestibleReason.Unreadable,
                documentPath,
                "the PDF parser could not read the document",
                error);
        }

        // The claim and the delivery, reconciled. TextLayer's contract is that it holds a string for
        // every page of the document, and every page number below is derived from position in this
        // list - so an enumeration that stopped early would produce a text layer that is short,
        // internally consistent, and wrong, with grounding rejecting every snippet from the pages
        // that never arrived. This is the check that actually defends that claim.
        if (pages.Count != declaredPageCount)
        {
            throw Refuse(
                DocumentNotIngestibleReason.Unreadable,
                documentPath,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the document declares {declaredPageCount} pages but yielded {pages.Count}"),
                innerException: null);
        }

        return Task.FromResult(new TextLayer(document.DocumentId, pages));
    }

    /// <summary>
    /// Opens the document, logging a structured event if the file system refuses.
    /// </summary>
    /// <remarks>
    /// Symmetric with <see cref="DocumentIngestService"/>'s read-failure event, and for the same
    /// reason: a failure this code did not decide - a share that disappeared mid-run, a denied ACL -
    /// would otherwise leave no trace in the security log at all, because nothing here treats it as a
    /// refusal. Only the exception's type is recorded; its <c>Message</c> routinely embeds the raw
    /// path, and this is the one string on the path into the log that nothing has sanitised. The
    /// exception itself propagates unaltered, because a caller diagnosing a share outage needs the
    /// real failure.
    /// </remarks>
    private FileStream OpenForReading(string documentPath)
    {
        // FileShare.Read, and RandomAccess rather than SequentialScan: PDFPig seeks - it reads the
        // trailer and the cross-reference table before it reads any page - so telling the OS to
        // prefetch linearly would be a hint about behaviour this file will not exhibit.
        FileStreamOptions readOptions = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.RandomAccess,
        };

        try
        {
            return new FileStream(documentPath, readOptions);
        }
        catch (Exception error)
        {
            LogTextLayerOpenFailed(
                _logger,
                PathBoundary.ForLog(documentPath),
                error.GetType().FullName ?? error.GetType().Name);

            throw;
        }
    }

    /// <summary>
    /// Logs the refusal as a structured event and returns it for the caller to throw.
    /// </summary>
    /// <remarks>
    /// Returning rather than throwing keeps <c>throw Refuse(...)</c> at the call site, as elsewhere in
    /// ingest, and puts every route to a text-layer refusal through one logging site.
    /// <paramref name="detail"/> is a constant chosen here, never text from the file or from the
    /// parser: the parser's own message is preserved on the inner exception for a caller that wants
    /// it, but only the exception's type name reaches the log.
    /// </remarks>
    private DocumentNotIngestibleException Refuse(
        DocumentNotIngestibleReason reason,
        string documentPath,
        string detail,
        Exception? innerException)
    {
        LogTextLayerRefused(
            _logger,
            reason,
            detail,
            PathBoundary.ForLog(documentPath),
            innerException?.GetType().FullName ?? "none");

        return new DocumentNotIngestibleException(reason, documentPath, byteLength: null, innerException);
    }

    /// <summary>Structured event for a document whose text layer could not be produced.</summary>
    /// <remarks>
    /// The path has already been through <see cref="PathBoundary.ForLog"/>, which is what makes
    /// naming it in the template safe; see the equivalent note on <see cref="DocumentIngestService"/>'s
    /// events. The failure is recorded by type only - a parser message describes the file's internal
    /// structure and is the one string here that nothing has sanitised.
    /// </remarks>
    [LoggerMessage(
        EventId = 1013,
        Level = LogLevel.Error,
        Message = "Ingest could not extract a text layer: {Reason} - {Detail}. Document='{DocumentPath}', failure={FailureType}.")]
    private static partial void LogTextLayerRefused(
        ILogger logger,
        DocumentNotIngestibleReason reason,
        string detail,
        string documentPath,
        string failureType);

    /// <summary>Structured event for a document that could not be opened for extraction.</summary>
    /// <remarks>
    /// Its own event id rather than a reuse of <see cref="LogTextLayerRefused"/>'s: this is not a
    /// refusal this code decided and carries no <see cref="DocumentNotIngestibleReason"/>, so folding
    /// the two onto one id would leave an alert rule unable to tell "the supplier's document is bad"
    /// from "our share is gone".
    /// </remarks>
    [LoggerMessage(
        EventId = 1015,
        Level = LogLevel.Error,
        Message = "Ingest could not open a document for text layer extraction. Document='{DocumentPath}', failure={FailureType}.")]
    private static partial void LogTextLayerOpenFailed(ILogger logger, string documentPath, string failureType);
}
