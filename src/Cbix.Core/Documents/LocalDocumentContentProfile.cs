using System.Collections.Concurrent;
using System.Globalization;

using Cbix.Core.Ingest;

using Microsoft.Extensions.AI;

namespace Cbix.Core.Documents;

/// <summary>
/// Shared behaviour of the document-content profiles that build their content entirely from local
/// inputs: the generic-vision profile (design 5.1, story S01-06) and the text-only fallback (story
/// S01-07).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a base class and not two independent implementations.</b>
/// <see cref="IDocumentContentProvider"/>'s contract puts four rules on every implementation -
/// memoise per <see cref="DocumentReference.DocumentId"/>, be safe under concurrent calls, issue a
/// handle whose profile name matches the capabilities, re-prepare rather than fail on an
/// unredeemable handle - and its own remarks note that "each of S01-05, S01-06 and S01-07 has to
/// get this right independently". Two of those three differ only in whether page images are
/// appended. Duplicating the lifecycle into both is how one of them silently drifts; the difference
/// between them is a single overridable step, so that is what the derived types override.
/// </para>
/// <para>
/// <b>What every local profile shares beyond the lifecycle.</b> Neither has any provider-side
/// artefact, so both issue a handle with a <see langword="null"/>
/// <see cref="DocumentContentHandle.ProviderToken"/> and both rebuild from the file on a resume.
/// That is cheap and correct here in a way it would not be for a profile that uploads: the
/// expensive, non-deterministic work the resume guarantee exists to protect is model calls, and a
/// local re-parse and re-render of a handful of pages costs milliseconds of CPU and nothing at all
/// in tokens. Redemption for these profiles is therefore not "reuse a remote object" but "there was
/// never anything remote to reuse", which is exactly the case
/// <see cref="DocumentContentHandle.ProviderToken"/> documents as storing <see langword="null"/>.
/// </para>
/// <para>
/// <b>Text is emitted one block per page, with the page number in a block of its own.</b> Two
/// decisions, both driven by Sprint 02 rather than by tidiness:
/// </para>
/// <para>
/// <em>Per page, not one concatenated string.</em> Every extracted scalar carries a
/// <c>SourcePage</c> (design 5.4), and the validator's grounding gate looks for the snippet on the
/// page the agent named (design 5.6). A single blob would leave the model to infer page boundaries
/// from whatever the document happens to print in its footers, and a page it inferred wrongly is a
/// snippet the gate rejects on a page it was actually read from correctly.
/// </para>
/// <para>
/// <em>The marker is a separate block from the page text.</em> The page text block is byte-identical
/// to <see cref="TextLayer.GetPage"/>, because that is the corpus grounding does verbatim
/// containment against. Prefixing the marker onto it would put text into the very block the prompts
/// instruct the model to copy from verbatim - and a snippet that came back carrying "--- Page 2 ---"
/// would fail grounding despite being an honest copy of what the model was shown. Keeping the
/// marker adjacent but separate gives the model the page number without contaminating the text it
/// is told to quote.
/// </para>
/// <para>
/// <b>Required lifetime: one instance per workflow-run scope</b>, as
/// <see cref="IDocumentContentProvider"/> states. The memo below lives in the instance and its
/// eviction bound is the run scope itself.
/// </para>
/// </remarks>
public abstract class LocalDocumentContentProfile : IDocumentContentProvider
{
    /// <summary>
    /// How long one document's preparation may run before it is abandoned, when no deadline is
    /// configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Generous on purpose, because it is a backstop rather than a budget. The renderer's aggregate
    /// megapixel ceiling already bounds a legitimate document to roughly 18 seconds of rasterisation
    /// at measured throughput, and a real country manual - a handful of A4 pages - is under a second.
    /// Five minutes is about seventeen times the worst case the ceilings admit, so it will not fire
    /// on slow hardware or a loaded host; it fires when something is wrong in a way the pixel
    /// arithmetic could not predict.
    /// </para>
    /// <para>
    /// It is not a substitute for those ceilings and must not be treated as one. A deadline only
    /// stops work after it has been running - it cannot un-allocate a 3 GB bitmap, and a pipeline
    /// relying on it alone would spend the full five minutes on every hostile document instead of
    /// refusing it in microseconds.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan DefaultPreparationDeadline = TimeSpan.FromMinutes(5);

    private readonly ITextLayerExtractor _textLayerExtractor;
    private readonly TimeSpan _preparationDeadline;

    /// <summary>
    /// One preparation per document, kept as the in-flight task rather than the finished value so
    /// that concurrent callers join the same work instead of racing to duplicate it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The key is <see cref="DocumentReference.DocumentId"/> alone</b>, as the port requires: the
    /// identity is a content hash, so two references sharing it are the same bytes, and keying on
    /// the location or the display name would let a re-registered path pay for a second render of a
    /// document already prepared.
    /// </para>
    /// <para>
    /// <b>A failed preparation is evicted rather than cached.</b> A transient failure is one the
    /// port expects the caller to retry with backoff, and a memo that remembered the exception
    /// would turn "retry" into "get the same failure instantly, forever" for the rest of the run.
    /// </para>
    /// </remarks>
    private readonly ConcurrentDictionary<string, Lazy<Task<DocumentContent>>> _prepared =
        new(StringComparer.Ordinal);

    /// <summary>Initialises a new <see cref="LocalDocumentContentProfile"/>.</summary>
    /// <param name="textLayerExtractor">
    /// Produces the per-page text this profile sends.
    /// <para>
    /// <b>Why the extractor and not the already-extracted <see cref="TextLayer"/>.</b> Ingest does
    /// extract one before any profile is called, so consuming that would be the cheaper wiring - but
    /// <see cref="PrepareAsync"/> receives only a <see cref="DocumentReference"/>, so the text layer
    /// would have to arrive as constructor state populated mid-run by ingest. That makes the profile
    /// correct only if ingest ran first and only for the one document it ran for, an ordering
    /// nothing enforces and whose failure mode is presenting one document's text under another's
    /// identity. Depending on the port instead keeps the profile correct standing alone, and the
    /// duplicate parse it implies is a local, offline, millisecond-scale cost - not the provider
    /// call the "one upload per document" rule exists to protect. S01-12 can collapse it to a single
    /// parse without touching this class, by registering a memoising decorator over
    /// <see cref="ITextLayerExtractor"/> in the run scope that ingest and the profiles both resolve;
    /// the seam is already decorator-shaped, which is how the existing suite counts extractions.
    /// </para>
    /// </param>
    /// <param name="preparationDeadline">
    /// How long one document's preparation - text extraction plus any rendering - may run before it
    /// is abandoned, or <see langword="null"/> for <see cref="DefaultPreparationDeadline"/>.
    /// <para>
    /// <b>This is the backstop behind the renderer's pre-allocation ceilings, not the primary
    /// control.</b> Those ceilings refuse an over-large document before any work starts, which is
    /// always better: no work is wasted and the refusal is deterministic. The deadline exists for
    /// what an estimate cannot cover - a host far slower than the one the budgets were measured on,
    /// a pathological document that renders slowly for its pixel count, a collaborator that hangs
    /// on something other than pixels.
    /// </para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="textLayerExtractor"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preparationDeadline"/> is not positive.</exception>
    protected LocalDocumentContentProfile(
        ITextLayerExtractor textLayerExtractor,
        TimeSpan? preparationDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(textLayerExtractor);

        if (preparationDeadline is { } deadline)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero, nameof(preparationDeadline));
        }

        _textLayerExtractor = textLayerExtractor;
        _preparationDeadline = preparationDeadline ?? DefaultPreparationDeadline;
    }

    /// <summary>
    /// Gets how faithfully this profile presents a document. The stable
    /// <see cref="DocumentContentCapabilities.ProfileName"/> on it is also the name its handles are
    /// issued under and redeemed against.
    /// </summary>
    /// <remarks>
    /// <b>Protected, not public.</b> This is the extensibility contract - what a derived profile
    /// must declare about itself - and not part of the port's surface. A caller reads the same
    /// descriptor off <see cref="DocumentContent.Capabilities"/> on the value it was returned, which
    /// is the copy that provably describes the content in its hands; a caller that instead asked a
    /// profile instance what it can do would be branching on the profile, and
    /// <see cref="IDocumentContentProvider"/> says plainly that no caller may do that. Exporting it
    /// publicly invited exactly that branch.
    /// </remarks>
    protected abstract DocumentContentCapabilities Capabilities { get; }

    /// <inheritdoc />
    public async Task<DocumentContent> PrepareAsync(
        DocumentReference document,
        DocumentContentHandle? resumeFrom = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        // Called for its guard, not its answer. A handle from another profile, or from a run whose
        // remote artefact has since vanished, is an expected operational state that this profile
        // answers by preparing again - it has nothing remote to redeem in the first place. The one
        // case that is never recoverable, a handle belonging to a DIFFERENT document, throws from
        // here, because both of its silent outcomes are corruption: content prepared from one
        // document returned under another's identity, and every value extracted from it recorded
        // against the wrong source.
        resumeFrom?.IsRedeemableBy(document, Capabilities.ProfileName);

        Lazy<Task<DocumentContent>> preparation = GetOrStartPreparation(document);

        // WaitAsync, rather than passing the caller's token into the shared work. One preparation is
        // joined by the seven-way section fan-out; if the token flowed into the shared task, the
        // first agent to be cancelled would cancel the document preparation the other six are
        // waiting on. This way a caller's cancellation abandons that caller's wait and nobody
        // else's.
        //
        // What the abandoned work then costs, corrected: an earlier version of this comment said
        // "milliseconds", which was true of the documents this pipeline expects and wrong by five
        // orders of magnitude at the ceilings it actually admits - a document sized to the aggregate
        // budget is about 18 seconds of rendering, and before that budget existed the admissible
        // worst case was 24 minutes. The honest bound is: at most the profile's own preparation
        // deadline, and in practice whatever the renderer's pre-allocation ceilings permit. That is
        // a bounded amount of CPU nobody is waiting for, not a leak - but it is seconds, not
        // milliseconds, and the deadline is what makes the sentence true at all.
        //
        // No eviction logic here any more: see GetOrStartPreparation, which attaches it to the task
        // itself. Evicting from a caller's catch block could only ever act on what that caller
        // happened to observe, and the failure it must survive - a run-level teardown cancelling
        // every observer at once, with the shared work faulting a moment later - is precisely the
        // case where no caller observes the fault at all.
        return await preparation.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the memo entry for a document, starting the preparation if this is the first ask.
    /// </summary>
    /// <remarks>
    /// The eviction-on-failure rule lives here, bound to the task rather than to any caller's view of
    /// it, and that placement is the whole point. Two earlier arrangements failed:
    /// <list type="bullet">
    /// <item>evicting in a caller's <c>catch</c> filtered on exception type, which kept a cancelled
    /// shared task memoised forever;</item>
    /// <item>evicting in a caller's <c>catch</c> filtered on that caller's token, which has a blind
    /// spot for the realistic teardown - every observer holds a token linked to one run-level source,
    /// so all of them are cancelled simultaneously and none is left to notice the fault. Worse, a
    /// caller can abandon its wait <em>before</em> the shared work fails, so at the moment it looks
    /// the task has not faulted yet.</item>
    /// </list>
    /// A continuation on the task itself has no blind spot: it runs when the work finishes badly,
    /// whether or not anyone is still watching, and whatever they were doing at the time.
    /// </remarks>
    private Lazy<Task<DocumentContent>> GetOrStartPreparation(DocumentReference document)
    {
        return _prepared.GetOrAdd(
            document.DocumentId,
            static (_, state) => CreateEntry(state.Profile, state.Document),
            (Profile: this, Document: document));

        static Lazy<Task<DocumentContent>> CreateEntry(LocalDocumentContentProfile profile, DocumentReference document)
        {
            // The Lazy has to refer to itself, because the eviction has to remove THIS entry and not
            // merely the key: another caller may already have evicted it and started a fresh
            // attempt, and removing that one would discard work in flight. Assigned after
            // construction and captured by the closure - safe because the factory cannot run before
            // the assignment completes, since nothing can read .Value until GetOrAdd has returned
            // the object.
            Lazy<Task<DocumentContent>>? entry = null;

            entry = new Lazy<Task<DocumentContent>>(
                () => profile.StartAndEvictOnFailure(document, entry!),
                LazyThreadSafetyMode.ExecutionAndPublication);

            return entry;
        }
    }

    /// <summary>Starts one preparation, and arranges for a failed one to leave the memo.</summary>
    private Task<DocumentContent> StartAndEvictOnFailure(DocumentReference document, Lazy<Task<DocumentContent>> entry)
    {
        // Task.Run, and it is the difference between the cancellation guarantee in PrepareAsync
        // being true and being a comment. PrepareCoreAsync's collaborators do real synchronous work
        // - a PDFPig parse, a PDFium render - before they reach an await, and calling it directly
        // here would run all of that INSIDE the Lazy's initialisation lock, on whichever thread
        // arrived first. Lazy.Value would then not return until the whole preparation had finished,
        // every other caller would block on the lock rather than await it, and WaitAsync would
        // receive an already-completed task and never observe a token. Measured consequence of the
        // earlier shape: joiners occupied pool threads for the full parse-and-render and could not
        // be cancelled at all. Running the body on the pool makes Lazy.Value return a hot task
        // immediately, which is what makes WaitAsync a genuine cancellation point.
        Task<DocumentContent> preparation = Task.Run(() => PrepareCoreAsync(document));

        // NotOnRanToCompletion: faulted AND cancelled both evict. A memo entry that will never yield
        // content is worthless whichever way it died, and remembering it turns one transient failure
        // into a permanent one for the rest of the run.
        //
        // Fire-and-forget by design, and it cannot swallow anything: the body only removes a
        // dictionary entry, and every caller still observes the task's own exception through its own
        // await. ExecuteSynchronously because that body is a single interlocked removal - handing it
        // to the scheduler would cost more than doing it.
        _ = preparation.ContinueWith(
            static (_, state) =>
            {
                (ConcurrentDictionary<string, Lazy<Task<DocumentContent>>> memo, string documentId, Lazy<Task<DocumentContent>> entry) =
                    ((ConcurrentDictionary<string, Lazy<Task<DocumentContent>>>, string, Lazy<Task<DocumentContent>>))state!;

                // Compare-and-remove, not TryRemove(key): another caller may already have evicted
                // this entry and started a fresh attempt, and removing that one would discard work
                // in flight.
                ICollection<KeyValuePair<string, Lazy<Task<DocumentContent>>>> entries = memo;
                entries.Remove(new KeyValuePair<string, Lazy<Task<DocumentContent>>>(documentId, entry));
            },
            (_prepared, document.DocumentId, entry),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return preparation;
    }

    /// <summary>
    /// Appends this profile's own content blocks for one page, after the page's marker and text
    /// blocks have been added.
    /// </summary>
    /// <param name="content">The block list being built, in presentation order.</param>
    /// <param name="logicalPageNumber">The 1-based page these blocks describe.</param>
    /// <param name="pageImages">
    /// The rendered page images, or <see langword="null"/> when this profile requested none. Indexed
    /// by <see cref="PageImage.LogicalPageNumber"/> rather than by position, so that a renderer that
    /// returned pages out of order cannot silently attach page 4's pixels to page 3's text.
    /// </param>
    /// <remarks>
    /// The only difference between the two local profiles, expressed as the only thing they
    /// override. The text-only profile does not override it at all.
    /// </remarks>
    protected virtual void AppendPageContent(
        IList<AIContent> content,
        int logicalPageNumber,
        IReadOnlyDictionary<int, PageImage>? pageImages)
    {
    }

    /// <summary>
    /// Renders the document's pages, or returns <see langword="null"/> when this profile presents no
    /// visual content.
    /// </summary>
    /// <param name="document">The document to render.</param>
    /// <param name="cancellationToken">Token that cancels the render.</param>
    /// <returns>The rendered pages keyed by logical page number, or <see langword="null"/>.</returns>
    protected virtual Task<IReadOnlyList<PageImage>?> RenderPagesAsync(
        DocumentReference document,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PageImage>?>(null);

    /// <summary>Builds the content for a document that is not already memoised.</summary>
    private async Task<DocumentContent> PrepareCoreAsync(DocumentReference document)
    {
        TextLayer textLayer;
        IReadOnlyList<PageImage>? pageImages;

        // A deadline the PROFILE owns, not the caller's token, and the distinction is the whole
        // design. The severed-token arrangement stands: a caller's cancellation must never reach the
        // shared work, because one agent of the seven-way fan-out giving up would otherwise cancel
        // the preparation the other six are waiting on. But the previous code passed
        // CancellationToken.None here, which severed the token and put nothing in its place - so the
        // renderer's between-pages cancellation check was dead code on the production path, and a
        // runaway render had no way to stop at all.
        //
        // This token is nobody's caller and everybody's ceiling: it belongs to this one preparation,
        // it fires only on elapsed time, and it makes the renderer's check reachable.
        using CancellationTokenSource deadline = new(_preparationDeadline);

        try
        {
            textLayer = await _textLayerExtractor.ExtractAsync(document, deadline.Token).ConfigureAwait(false);
            pageImages = await RenderPagesAsync(document, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested)
        {
            // Translated rather than propagated. A bare OperationCanceledException leaving here would
            // be read by PrepareAsync's joiners as their own cancellation, and by the workflow as a
            // graceful stop, when in fact the document defeated its own resource budget.
            //
            // Not transient: the work is bounded by the document, so a retry buys another full
            // deadline of the same rendering to reach the same place. Review is where it belongs.
            throw new DocumentPreparationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    // The TimeSpan itself, not a rounded seconds count: "F0" rendered a 250 ms
                    // deadline as "0s", which reads like a misconfiguration report rather than a
                    // timeout and would send an operator hunting for a zero in the config.
                    $"Document '{document.DocumentId}' could not be presented within the preparation deadline of {_preparationDeadline}."),
                isTransient: false,
                error);
        }
        catch (DocumentNotIngestibleException error)
        {
            // A property of the document's bytes, and deterministic in them: the same file refused
            // today is refused identically tomorrow, so retrying it would burn attempts to reach the
            // same answer. Review is where it belongs.
            throw new DocumentPreparationException(
                $"Document '{document.DocumentId}' could not be presented: {error.Reason}.",
                isTransient: false,
                error);
        }
        catch (PageRenderFaultException error)
        {
            // The rasteriser failed, as opposed to the document being unrenderable. Not transient -
            // a native library that could not render a page will not render it moments later, and a
            // retry loop against a faulting renderer is how one hostile document becomes sustained
            // pressure on the host - but deliberately NOT folded into the refusal arm above, because
            // the two want different human responses. A refusal is "this document is bad"; this is
            // "the renderer is bad, on untrusted input, in native code the dependency audit cannot
            // see". The renderer has already emitted the structured event; this preserves the
            // distinction in what the workflow records.
            throw new DocumentPreparationException(
                $"Document '{document.DocumentId}' could not be presented: the page rasteriser faulted.",
                isTransient: false,
                error);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            // Non-transient by the port's own list ("an unreadable file"). Normally unreachable:
            // ingest registered and read this document before any profile saw it, so a file missing
            // here means the ingest share was mutated underneath a live run - a deployment fault to
            // put in front of a human, not something a backoff will fix.
            //
            // DirectoryNotFoundException is caught HERE rather than falling through to the
            // IOException arm below, and the placement is the fix for a real inconsistency. Both of
            // these derive from IOException, so ordering alone decided their fate: a missing file
            // was permanent while a missing directory was transient, which is incoherent - a
            // vanished ingest root is not more recoverable than a vanished file inside it, it is
            // less. It also disagreed with PdfPigTextLayerExtractor, which groups the two as one
            // "the document is not there" outcome. They are grouped here for the same reason.
            throw new DocumentPreparationException(
                $"Document '{document.DocumentId}' is no longer present at its registered location.",
                isTransient: false,
                error);
        }
        catch (UnauthorizedAccessException error)
        {
            // A property of the configuration - the service principal's rights on the ingest share -
            // and a retry would only repeat the same denial.
            throw new DocumentPreparationException(
                $"Document '{document.DocumentId}' could not be read: access was denied.",
                isTransient: false,
                error);
        }
        catch (IOException error)
        {
            // Everything else in the IOException family, the two "it is not there" cases above
            // having already been taken: the share dropped, the handle died, the file system failed.
            // TRANSIENT, and deliberately separated from the refusal above - an infrastructure fault
            // is not a statement about the document, and routing it to review would tell a human a
            // supplier sent a bad file when the network was at fault.
            throw new DocumentPreparationException(
                $"Document '{document.DocumentId}' could not be read from its registered location.",
                isTransient: true,
                error);
        }

        // OutOfMemoryException is deliberately absent from those filters. It is the observable
        // symptom of a decompression or rasterisation bomb, and wrapping it as a preparation failure
        // would file a resource-exhaustion attack indicator as a data-quality outcome, hiding it
        // from whoever watches for attacks. The same rule the ingest ports state.

        if (pageImages is not null && pageImages.Count != textLayer.PageCount)
        {
            // Two local readers of the same file that disagree about how many pages it has. Neither
            // answer can be trusted after that, and the alternative - presenting whichever count is
            // smaller - would silently drop pages from a document while reporting full fidelity.
            throw new DocumentPreparationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Document '{document.DocumentId}' yielded {textLayer.PageCount} text pages but {pageImages.Count} rendered pages; the two local readers disagree about the document."),
                isTransient: false);
        }

        // Built defensively rather than with ToDictionary, which throws a raw ArgumentException on a
        // duplicate key. That call sat outside every classification filter above, so a renderer
        // returning two images numbered page 3 would have escaped this class as an unclassified
        // argument error - the one failure shape a caller has no contract for, from the same
        // untrusted-input path everything else here is careful about. Defended like its sibling, the
        // missing-page check in AppendPageContent.
        Dictionary<int, PageImage>? imagesByPage = null;

        if (pageImages is not null)
        {
            imagesByPage = new Dictionary<int, PageImage>(pageImages.Count);

            foreach (PageImage image in pageImages)
            {
                if (!imagesByPage.TryAdd(image.LogicalPageNumber, image))
                {
                    throw new DocumentPreparationException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Document '{document.DocumentId}' produced two rendered images for logical page {image.LogicalPageNumber}; page provenance cannot be attributed."),
                        isTransient: false);
                }
            }
        }

        List<AIContent> content = [];
        for (int page = TextLayer.FirstLogicalPageNumber; page < TextLayer.FirstLogicalPageNumber + textLayer.PageCount; page++)
        {
            content.Add(new TextContent(FormatPageMarker(page, textLayer.PageCount)));
            content.Add(new TextContent(textLayer.GetPage(page)));

            int beforePageContent = content.Count;
            AppendPageContent(content, page, imagesByPage);

            // The capability flag and the blocks, reconciled - the same kind of check as the
            // page-count reconciliation above, and it exists because the two protected hooks can
            // disagree silently. RenderPagesAsync and AppendPageContent are overridden
            // independently; a derived profile that overrides the first and forgets the second
            // renders every page, throws the pixels away, and returns text-only content while
            // Capabilities still reports full visual fidelity. Nothing else in the system would
            // notice: the eval harness would attribute that run's matrix accuracy to a visual
            // profile, which is precisely the mis-measurement the degraded flag exists to prevent.
            if (Capabilities.IncludesVisualContent
                && !ContainsNonTextBlock(content, beforePageContent))
            {
                throw new DocumentPreparationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Document '{document.DocumentId}' is presented by a profile reporting visual content, but logical page {page} carries no visual block."),
                    isTransient: false);
            }
        }

        return new DocumentContent(
            content,
            Capabilities,
            new DocumentContentHandle(document.DocumentId, Capabilities.ProfileName, providerToken: null));
    }

    /// <summary>Reports whether any block added at or after <paramref name="from"/> is something other than text.</summary>
    /// <remarks>
    /// Deliberately "not <see cref="TextContent"/>" rather than "is <see cref="DataContent"/>". The
    /// question this answers is whether the profile contributed anything beyond the text the base
    /// class already added, and a future visual profile might present a page as a hosted image
    /// reference rather than as inline bytes. Testing for the absence of the thing we know is not
    /// visual keeps the check correct for block types nobody has written yet.
    /// </remarks>
    private static bool ContainsNonTextBlock(List<AIContent> content, int from)
    {
        for (int index = from; index < content.Count; index++)
        {
            if (content[index] is not TextContent)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Formats the page marker that precedes each page's text.</summary>
    /// <remarks>
    /// The total is included because a model that can see how many pages there are stops guessing at
    /// whether it has reached the end, and the extraction prompts ask for logical page numbers "as
    /// shown in a PDF viewer" - which is what this states.
    /// </remarks>
    private static string FormatPageMarker(int logicalPageNumber, int pageCount) =>
        string.Create(CultureInfo.InvariantCulture, $"--- Page {logicalPageNumber} of {pageCount} ---");
}
