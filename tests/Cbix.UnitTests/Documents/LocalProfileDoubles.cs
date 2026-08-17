using Cbix.Core.Documents;
using Cbix.Core.Ingest;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// A text layer extractor that counts its calls and can be told to fail.
/// </summary>
/// <remarks>
/// Stubbed here, unlike in the BDD scenarios, and deliberately: these tests are about the profile's
/// own behaviour - memoisation, handle issuing, failure classification - and none of it is about
/// PDFPig. Driving them through a real PDF would make every assertion depend on a fixture and hide
/// the failure cases entirely, since a real extractor cannot be asked to raise an
/// <see cref="IOException"/> on demand. What PDFPig actually produces from the real specimen is
/// asserted by the S01-06 and S01-07 scenarios.
/// </remarks>
public sealed class StubTextLayerExtractor(params string[] pages) : ITextLayerExtractor
{
    private readonly string[] _pages = pages.Length == 0 ? ["page one text"] : pages;
    private int _callCount;

    /// <summary>Gets how many times <see cref="ExtractAsync"/> was entered.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Gets how many pages this extractor reports, so a paired renderer can agree with it.</summary>
    public int PageCount => _pages.Length;

    /// <summary>Gets or sets a factory for the exception to throw instead of returning a text layer.</summary>
    public Func<Exception>? Failure { get; set; }

    /// <summary>
    /// Gets or sets work to await inside the call, used to hold a preparation open while a test
    /// observes what other callers do.
    /// </summary>
    /// <remarks>
    /// <b>Asynchronous, not a blocking wait, and that is not a style preference.</b> The profile now
    /// runs preparations on the thread pool, so a double that blocked its thread would hold a pool
    /// thread for the whole gate. With several such tests running in parallel - xUnit runs classes
    /// concurrently, and every one of these contract tests is inherited by two profiles - that
    /// starves the pool and the suite stalls until the pool's one-thread-per-second injection
    /// catches up. Measured: converting these gates from ManualResetEventSlim to awaited tasks is
    /// what stopped the suite hanging.
    /// </remarks>
    public Func<CancellationToken, Task>? Block { get; set; }

    /// <summary>
    /// Waits until the most recent call has returned or thrown.
    /// </summary>
    /// <remarks>
    /// Lets a test assert on what happens AFTER a preparation fails without sleeping for an
    /// arbitrary interval. The eviction under test is attached to the task rather than to any
    /// caller, so there is otherwise no caller-side moment at which it is known to have run.
    /// </remarks>
    public Task WaitForCallToFinishAsync() => Volatile.Read(ref _finished).Task;

    private TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public async Task<TextLayer> ExtractAsync(DocumentReference document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        Interlocked.Increment(ref _callCount);
        TaskCompletionSource finished = Volatile.Read(ref _finished);

        try
        {
            if (Block is not null)
            {
                await Block(cancellationToken).ConfigureAwait(false);
            }

            if (Failure is not null)
            {
                throw Failure();
            }

            return new TextLayer(document.DocumentId, _pages);
        }
        finally
        {
            // Signalled on both paths, and the source is swapped first so a later call can be
            // awaited independently of this one.
            Volatile.Write(ref _finished, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            finished.TrySetResult();
        }
    }
}

/// <summary>A page image renderer that counts its calls and returns synthetic bytes.</summary>
/// <remarks>
/// The page count is a delegate rather than a number so a renderer can be wired to agree with
/// whatever text layer its paired extractor happens to produce. The generic-vision profile
/// reconciles the two counts and fails when they differ, which is correct behaviour and would
/// otherwise make every inherited contract test that varies the page count fail for the wrong
/// reason.
/// </remarks>
public sealed class StubPageImageRenderer(Func<int> pageCount) : IPageImageRenderer
{
    private int _callCount;

    /// <summary>Initialises a renderer that always reports a fixed page count.</summary>
    public StubPageImageRenderer(int pageCount)
        : this(() => pageCount)
    {
    }

    /// <summary>Gets how many times <see cref="RenderAsync"/> was entered.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>Gets or sets a factory for the exception to throw instead of returning images.</summary>
    public Func<Exception>? Failure { get; set; }

    /// <summary>
    /// Gets or sets work to await before returning, used to hold a render open while a test observes
    /// the deadline or a cancellation.
    /// </summary>
    /// <remarks>
    /// Takes the token it was given so this double behaves as a well-written renderer does: the
    /// production renderer checks between pages, and a stub that ignored the token would make the
    /// deadline test pass by timing out rather than by the mechanism under test.
    /// </remarks>
    public Func<CancellationToken, Task>? Block { get; set; }

    /// <summary>Gets or sets a value indicating whether every image should claim the first page's number.</summary>
    /// <remarks>Exercises the duplicate-page defence, which no correct renderer would trigger.</remarks>
    public bool RepeatFirstPageNumber { get; set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PageImage>> RenderAsync(
        DocumentReference document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        Interlocked.Increment(ref _callCount);

        if (Block is not null)
        {
            await Block(cancellationToken).ConfigureAwait(false);
        }

        if (Failure is not null)
        {
            throw Failure();
        }

        List<PageImage> images = [];
        for (int page = TextLayer.FirstLogicalPageNumber; page < TextLayer.FirstLogicalPageNumber + pageCount(); page++)
        {
            images.Add(new PageImage(
                RepeatFirstPageNumber ? TextLayer.FirstLogicalPageNumber : page,
                "image/png",
                new byte[] { 0x89, 0x50, 0x4E, 0x47, (byte)page }));
        }

        return images;
    }
}

/// <summary>Builds document references for the tests, all of which point at paths nothing opens.</summary>
public static class TestDocuments
{
    /// <summary>Creates a reference with the given identity.</summary>
    /// <remarks>
    /// The location is never opened by these tests - the extractor and renderer are stubs - so a
    /// path that need not exist is honest here. The profiles under test read only
    /// <see cref="DocumentReference.DocumentId"/> themselves and hand the reference to their
    /// collaborators.
    /// </remarks>
    public static DocumentReference Create(string documentId = "sha256:aaa", string fileName = "manual.pdf") =>
        new(documentId, new Uri($"file:///documents/{fileName}"), fileName);
}
