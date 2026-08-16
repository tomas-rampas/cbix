using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.AI;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// The behaviour <see cref="IDocumentContentProvider"/>'s contract requires of every local profile,
/// asserted once and run against each of them.
/// </summary>
/// <remarks>
/// <para>
/// The port's own remarks note that memoisation, concurrency safety, handle semantics and the
/// redemption policy have to be got right independently by each implementation - which is exactly
/// the shape of requirement that ends up copy-pasted into two test classes and then maintained in
/// one. Inheriting the assertions means a profile added later cannot quietly skip half of them: it
/// derives from this class or it has no contract coverage at all, and the difference is visible.
/// </para>
/// <para>
/// Profile-specific behaviour - images or no images, what the capability flag says - lives in the
/// derived classes, because that is the part that genuinely differs.
/// </para>
/// </remarks>
public abstract class LocalDocumentContentProfileContract
{
    /// <summary>Gets the name the profile under test must report and issue handles under.</summary>
    protected abstract string ExpectedProfileName { get; }

    /// <summary>Gets whether the profile under test presents pages visually.</summary>
    protected abstract bool ExpectsVisualContent { get; }

    /// <summary>Creates the profile under test over the supplied text layer extractor.</summary>
    protected abstract LocalDocumentContentProfile CreateProfile(ITextLayerExtractor textLayerExtractor);

    [Fact]
    public async Task PrepareAsync_NullDocument_Throws()
    {
        LocalDocumentContentProfile profile = CreateProfile(new StubTextLayerExtractor());

        await Assert.ThrowsAsync<ArgumentNullException>(() => profile.PrepareAsync(null!));
    }

    [Fact]
    public async Task PrepareAsync_CalledTwiceForOneDocument_PreparesOnce()
    {
        StubTextLayerExtractor extractor = new();
        LocalDocumentContentProfile profile = CreateProfile(extractor);
        DocumentReference document = TestDocuments.Create();

        DocumentContent first = await profile.PrepareAsync(document);
        DocumentContent second = await profile.PrepareAsync(document);

        // The same instance, not merely an equivalent one. The port hands one preparation to the
        // seven-way section fan-out; returning a fresh copy per call would satisfy "equivalent
        // content each time" while re-paying for the work the memo exists to avoid.
        Assert.Same(first, second);
        Assert.Equal(1, extractor.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_ForTheSameDocumentIdAtADifferentLocation_PreparesOnce()
    {
        // The memo key is DocumentId alone, as the port states: the identity is a content hash, so
        // two references sharing it are the same bytes. Keying on the whole reference would let a
        // re-registered path or a renamed file pay for a second preparation of a document already
        // prepared - and, for a profile that uploads, a second upload.
        StubTextLayerExtractor extractor = new();
        LocalDocumentContentProfile profile = CreateProfile(extractor);

        DocumentContent first = await profile.PrepareAsync(TestDocuments.Create("sha256:same", "one.pdf"));
        DocumentContent second = await profile.PrepareAsync(TestDocuments.Create("sha256:same", "two-renamed.pdf"));

        Assert.Same(first, second);
        Assert.Equal(1, extractor.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_ForDifferentDocuments_PreparesEach()
    {
        StubTextLayerExtractor extractor = new();
        LocalDocumentContentProfile profile = CreateProfile(extractor);

        DocumentContent first = await profile.PrepareAsync(TestDocuments.Create("sha256:one"));
        DocumentContent second = await profile.PrepareAsync(TestDocuments.Create("sha256:two"));

        Assert.NotSame(first, second);
        Assert.Equal(2, extractor.CallCount);
        Assert.Equal("sha256:one", first.Handle.DocumentId);
        Assert.Equal("sha256:two", second.Handle.DocumentId);
    }

    [Fact]
    public async Task PrepareAsync_CalledConcurrently_PreparesOnce()
    {
        // The reason the memo holds the in-flight task rather than the finished value. The section
        // agents fan out in parallel against one document, so "prepared once" has to survive
        // simultaneous entry, not merely sequential repetition - and a check-then-act memo passes
        // the sequential test and fails this one.
        const int Callers = 8;

        // A CountdownEvent as an arrival barrier, not a plain gate. The earlier version released
        // the gate as soon as the test thread reached Set(), which does not establish that any
        // caller had arrived yet: on an unlucky schedule the first caller could complete the whole
        // preparation before the eighth was created, and the test would then assert single
        // preparation against work that was never concurrent - passing for the wrong reason and
        // never failing on a genuinely racy memo. Now the preparation cannot proceed until every
        // caller has entered, so the contended path is the one actually measured.
        using CountdownEvent arrived = new(Callers);
        using ManualResetEventSlim release = new(initialState: false);

        StubTextLayerExtractor extractor = new()
        {
            OnCall = () => release.Wait(TimeSpan.FromSeconds(30)),
        };

        LocalDocumentContentProfile profile = CreateProfile(extractor);
        DocumentReference document = TestDocuments.Create();

        Task<DocumentContent>[] callers =
        [
            .. Enumerable.Range(0, Callers).Select(_ => Task.Run(() =>
            {
                arrived.Signal();
                return profile.PrepareAsync(document);
            })),
        ];

        Assert.True(
            arrived.Wait(TimeSpan.FromSeconds(30)),
            "Not every caller reached the profile, so this did not test concurrent preparation.");

        release.Set();
        DocumentContent[] results = await Task.WhenAll(callers);

        Assert.Equal(1, extractor.CallCount);
        Assert.All(results, result => Assert.Same(results[0], result));
    }

    [Fact]
    public async Task PrepareAsync_WhenOneJoinerCancels_TheOthersStillGetTheirContent()
    {
        // The guarantee the memo's comment claims, now actually testable. It was NOT true before:
        // the Lazy factory invoked the preparation directly, so all of its synchronous work - a
        // PDFPig parse, a PDFium render - ran inside the Lazy's initialisation lock on the first
        // caller's thread. Every joiner then blocked on that lock rather than awaiting a task, held
        // a pool thread for the whole duration, and could not be cancelled at all; WaitAsync only
        // ever saw an already-completed task. Running the body on the pool makes Lazy.Value hand
        // back a hot task, which is what turns WaitAsync into a real cancellation point.
        //
        // What this asserts: one caller walking away does not take the shared preparation with it.
        using ManualResetEventSlim release = new(initialState: false);

        StubTextLayerExtractor extractor = new()
        {
            OnCall = () => release.Wait(TimeSpan.FromSeconds(30)),
        };

        LocalDocumentContentProfile profile = CreateProfile(extractor);
        DocumentReference document = TestDocuments.Create();

        using CancellationTokenSource impatient = new();

        Task<DocumentContent> cancelling = profile.PrepareAsync(document, resumeFrom: null, impatient.Token);
        Task<DocumentContent> patient = profile.PrepareAsync(document);

        // Cancel while the preparation is genuinely still in flight - the extractor is parked on
        // the gate below, so neither caller can have completed.
        Assert.False(cancelling.IsCompleted);
        Assert.False(patient.IsCompleted);

        await impatient.CancelAsync();

        // The impatient caller's wait is abandoned...
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling);

        // ...while the shared work is untouched and still serves everyone else. If the token had
        // been passed into the preparation instead of into the wait, this is the assertion that
        // would fail: one agent of the seven-way fan-out giving up would have cancelled the
        // document preparation the other six were waiting on.
        release.Set();

        DocumentContent content = await patient;

        Assert.NotEmpty(content.Content);
        Assert.Equal(1, extractor.CallCount);

        // And the memo is not poisoned by the cancellation: a later caller gets the same prepared
        // content rather than a permanently cancelled task or a repeated preparation.
        Assert.Same(content, await profile.PrepareAsync(document));
        Assert.Equal(1, extractor.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_IssuesAHandleThatMatchesItsCapabilities()
    {
        LocalDocumentContentProfile profile = CreateProfile(new StubTextLayerExtractor());
        DocumentReference document = TestDocuments.Create();

        DocumentContent content = await profile.PrepareAsync(document);

        Assert.Equal(ExpectedProfileName, content.Capabilities.ProfileName);
        Assert.Equal(ExpectedProfileName, content.Handle.ProfileName);
        Assert.Equal(document.DocumentId, content.Handle.DocumentId);

        // Null, and it is not an oversight. These profiles create no provider-side artefact, so
        // there is nothing to redeem: a resume rebuilds the content from the local file, which is
        // cheap and correct because the work the resume guarantee protects is model calls.
        Assert.Null(content.Handle.ProviderToken);
    }

    [Fact]
    public async Task Capabilities_HonourTheDegradedInvariant()
    {
        LocalDocumentContentProfile profile = CreateProfile(new StubTextLayerExtractor());
        DocumentContent content = await profile.PrepareAsync(TestDocuments.Create());

        Assert.Equal(ExpectsVisualContent, content.Capabilities.IncludesVisualContent);

        if (!ExpectsVisualContent)
        {
            Assert.True(content.Capabilities.IsDegraded);

            // Asserted as forced rather than merely observed: the profile does not get to report
            // full fidelity without visual content even if a future edit passed the other pair of
            // booleans, because the type refuses to construct that way.
            ArgumentException refusal = Assert.Throws<ArgumentException>(
                () => new DocumentContentCapabilities(ExpectedProfileName, includesVisualContent: false, isDegraded: false));

            Assert.Equal("isDegraded", refusal.ParamName);
        }
    }

    [Fact]
    public async Task PrepareAsync_WithItsOwnHandle_ReturnsEquivalentContent()
    {
        LocalDocumentContentProfile profile = CreateProfile(new StubTextLayerExtractor());
        DocumentReference document = TestDocuments.Create();

        DocumentContent first = await profile.PrepareAsync(document);
        DocumentContent resumed = await profile.PrepareAsync(document, first.Handle);

        Assert.Same(first, resumed);
    }

    [Fact]
    public async Task PrepareAsync_WithAnotherProfilesHandle_PreparesAgainRatherThanFailing()
    {
        // A provider swap leaves handles in run state that were issued by a different profile. The
        // port calls that an expected operational state, not an error: the profile prepares again
        // and issues a new handle of its own.
        StubTextLayerExtractor extractor = new();
        LocalDocumentContentProfile profile = CreateProfile(extractor);
        DocumentReference document = TestDocuments.Create();

        DocumentContentHandle foreign = new(document.DocumentId, "some-other-profile", "token-from-elsewhere");

        DocumentContent content = await profile.PrepareAsync(document, foreign);

        Assert.Equal(ExpectedProfileName, content.Handle.ProfileName);
        Assert.Null(content.Handle.ProviderToken);
        Assert.Equal(1, extractor.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_WithAHandleForADifferentDocument_Throws()
    {
        // The one case that is never recoverable. Carrying on would return one document's content
        // under another document's identity, and record every value extracted from it against the
        // wrong source - the mis-attributed provenance the audit bar exists to prevent.
        LocalDocumentContentProfile profile = CreateProfile(new StubTextLayerExtractor());

        DocumentContentHandle other = new("sha256:a-different-document", ExpectedProfileName);

        await Assert.ThrowsAsync<ArgumentException>(
            () => profile.PrepareAsync(TestDocuments.Create("sha256:this-one"), other));
    }

    [Fact]
    public async Task PrepareAsync_WhenTheDocumentIsRefused_IsNotTransient()
    {
        StubTextLayerExtractor extractor = new()
        {
            Failure = () => new DocumentNotIngestibleException(DocumentNotIngestibleReason.Unreadable, "/documents/manual.pdf"),
        };

        LocalDocumentContentProfile profile = CreateProfile(extractor);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => profile.PrepareAsync(TestDocuments.Create()));

        // Deterministic in the document's bytes, so retrying reaches the same answer. Review, not
        // backoff.
        Assert.False(error.IsTransient);
        Assert.IsType<DocumentNotIngestibleException>(error.InnerException);
    }

    [Fact]
    public async Task PrepareAsync_WhenTheFileIsMissing_IsNotTransient()
    {
        StubTextLayerExtractor extractor = new()
        {
            Failure = () => new FileNotFoundException("gone", "/documents/manual.pdf"),
        };

        LocalDocumentContentProfile profile = CreateProfile(extractor);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => profile.PrepareAsync(TestDocuments.Create()));

        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task PrepareAsync_WhenAccessIsDenied_IsNotTransient()
    {
        StubTextLayerExtractor extractor = new()
        {
            Failure = () => new UnauthorizedAccessException("denied"),
        };

        LocalDocumentContentProfile profile = CreateProfile(extractor);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => profile.PrepareAsync(TestDocuments.Create()));

        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task PrepareAsync_WhenTheShareFails_IsTransient()
    {
        // The distinction the exception's IsTransient flag exists to carry. An IOException is the
        // infrastructure failing, not a statement about the document, so it is retried with backoff
        // rather than routed to a human who would be told a supplier sent a bad file.
        StubTextLayerExtractor extractor = new()
        {
            Failure = () => new IOException("the share dropped"),
        };

        LocalDocumentContentProfile profile = CreateProfile(extractor);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => profile.PrepareAsync(TestDocuments.Create()));

        Assert.True(error.IsTransient);
        Assert.IsType<IOException>(error.InnerException);
    }

    [Fact]
    public async Task PrepareAsync_WhenTheParseExhaustsMemory_PropagatesUnwrapped()
    {
        // Not wrapped, on purpose and for the same reason the ingest ports give: an
        // OutOfMemoryException is the observable symptom of a decompression or rasterisation bomb.
        // Filing it as a preparation failure would turn a resource-exhaustion attack indicator into
        // a data-quality outcome and hide it from whoever watches for attacks.
        StubTextLayerExtractor extractor = new()
        {
            Failure = () => new OutOfMemoryException(),
        };

        LocalDocumentContentProfile profile = CreateProfile(extractor);

        await Assert.ThrowsAsync<OutOfMemoryException>(() => profile.PrepareAsync(TestDocuments.Create()));
    }

    [Fact]
    public async Task PrepareAsync_AfterAFailure_TriesAgainRatherThanReplayingTheFailure()
    {
        // A memo that cached the faulted task would turn "retry with backoff" into "receive the
        // same failure instantly, for the rest of the run" - which would defeat the transient
        // classification asserted above.
        StubTextLayerExtractor extractor = new()
        {
            Failure = () => new IOException("the share dropped"),
        };

        LocalDocumentContentProfile profile = CreateProfile(extractor);
        DocumentReference document = TestDocuments.Create();

        await Assert.ThrowsAsync<DocumentPreparationException>(() => profile.PrepareAsync(document));

        extractor.Failure = null;
        DocumentContent content = await profile.PrepareAsync(document);

        Assert.Equal(2, extractor.CallCount);
        Assert.NotEmpty(content.Content);
    }

    [Fact]
    public async Task PrepareAsync_WithACancelledToken_Throws()
    {
        LocalDocumentContentProfile profile = CreateProfile(new StubTextLayerExtractor());

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => profile.PrepareAsync(TestDocuments.Create(), resumeFrom: null, cancelled.Token));
    }

    [Fact]
    public async Task PrepareAsync_EmitsEachPageAsItsOwnVerbatimTextBlockBehindAPageMarker()
    {
        // Two properties in one, both owed to Sprint 02. The page text is byte-identical to what
        // the extractor produced, because that is the corpus the grounding gate does verbatim
        // containment against; and the page number is stated in a block of its own, because a
        // marker prefixed onto the page text would end up inside snippets the model was told to
        // copy verbatim, failing grounding on pages it read correctly.
        StubTextLayerExtractor extractor = new("first page body", "second page body");
        LocalDocumentContentProfile profile = CreateProfile(extractor);

        DocumentContent content = await profile.PrepareAsync(TestDocuments.Create());

        List<string> texts = [.. content.Content.OfType<TextContent>().Select(block => block.Text)];

        Assert.Contains("first page body", texts, StringComparer.Ordinal);
        Assert.Contains("second page body", texts, StringComparer.Ordinal);

        int firstMarker = texts.FindIndex(text => text.Contains("Page 1", StringComparison.Ordinal));
        int firstBody = texts.FindIndex(text => string.Equals(text, "first page body", StringComparison.Ordinal));
        int secondMarker = texts.FindIndex(text => text.Contains("Page 2", StringComparison.Ordinal));
        int secondBody = texts.FindIndex(text => string.Equals(text, "second page body", StringComparison.Ordinal));

        Assert.True(firstMarker >= 0 && secondMarker >= 0, "The page markers are missing.");
        Assert.Equal(firstBody - 1, firstMarker);
        Assert.Equal(secondBody - 1, secondMarker);
        Assert.True(firstBody < secondMarker, "The pages are not in document order.");
    }

    [Fact]
    public async Task PrepareAsync_CarriesNoProviderPayload()
    {
        // These profiles exist to work with no provider SDK present, so the one legitimate route a
        // provider type takes into content - AIContent.RawRepresentation - has to be unused.
        LocalDocumentContentProfile profile = CreateProfile(new StubTextLayerExtractor());

        DocumentContent content = await profile.PrepareAsync(TestDocuments.Create());

        Assert.All(content.Content, block => Assert.Null(block.RawRepresentation));
    }
}
