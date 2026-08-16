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

    /// <summary>Gets or sets work to run inside the call, used to widen the window a concurrency test races in.</summary>
    public Action? OnCall { get; set; }

    /// <inheritdoc />
    public Task<TextLayer> ExtractAsync(DocumentReference document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        Interlocked.Increment(ref _callCount);
        OnCall?.Invoke();

        if (Failure is not null)
        {
            throw Failure();
        }

        return Task.FromResult(new TextLayer(document.DocumentId, _pages));
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

    /// <inheritdoc />
    public Task<IReadOnlyList<PageImage>> RenderAsync(
        DocumentReference document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        Interlocked.Increment(ref _callCount);

        if (Failure is not null)
        {
            throw Failure();
        }

        List<PageImage> images = [];
        for (int page = TextLayer.FirstLogicalPageNumber; page < TextLayer.FirstLogicalPageNumber + pageCount(); page++)
        {
            images.Add(new PageImage(page, "image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47, (byte)page }));
        }

        return Task.FromResult<IReadOnlyList<PageImage>>(images);
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
