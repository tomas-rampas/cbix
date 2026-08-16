using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Cbix.Core.Ingest;

/// <summary>
/// The local, per-page text of one document: the corpus the validator's grounding gate checks
/// extracted snippets against (design 5.1, 5.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Its only real contract is fidelity.</b> Grounding is a verbatim string-containment check -
/// "every <c>SourceSnippet</c> must appear verbatim in the PDFPig text layer" - so a text layer that
/// is prettier than the page it came from is not a better text layer, it is a broken one: every
/// character this type does not carry is a snippet the gate will reject as ungrounded even though
/// the model copied it correctly. Producers must therefore not trim, case-fold, collapse runs of
/// whitespace or strip punctuation. <see cref="PdfPigTextLayerExtractor"/> documents precisely what
/// its own extraction does and does not change.
/// </para>
/// <para>
/// <b>Pages are keyed by logical page number, 1-based</b> - the number a reader sees in a PDF
/// viewer, which is the numbering the extraction prompts mandate ("logical page numbers as shown in
/// a PDF viewer") and therefore the numbering an agent will report in
/// <c>ExtractedField.SourcePage</c>. The gate looks on the page the agent named, so this key has to
/// mean the same thing on both sides.
/// </para>
/// <para>
/// <b>Contiguity is structural, not checked.</b> The constructor takes the pages in order and
/// derives their numbers, so a text layer with a hole in it - page 3 missing while pages 2 and 4 are
/// present - cannot be built. That is what lets <see cref="PageCount"/> be trusted as "every page of
/// the document" rather than "however many pages someone happened to add".
/// </para>
/// <para>
/// <b>A page with no text is an empty string, never <see langword="null"/> and never absent.</b> A
/// scanned or purely graphical page genuinely has no text layer; recording that as an empty string
/// keeps "a string was produced for every page" true, and makes the grounding gate fail closed on it
/// (nothing is contained in an empty string) instead of skipping the page as unknown.
/// </para>
/// <para>
/// <b>A class rather than a record.</b> Records advertise value equality, and this type's contents
/// are an array: a compiler-generated <c>Equals</c> would compare it by reference and report two
/// character-identical text layers as different. Equality has no use here anyway - the identity that
/// matters is <see cref="DocumentId"/>, which is a content hash.
/// </para>
/// </remarks>
public sealed class TextLayer
{
    /// <summary>
    /// The number of the first page, as a reader sees it. Named rather than written as a bare
    /// <c>1</c> at each of the arithmetic sites below, because every one of them is a place where
    /// a 0-based slip would silently shift the whole document by one page.
    /// </summary>
    public const int FirstLogicalPageNumber = 1;

    private readonly string[] _pages;
    private readonly ReadOnlyCollection<string> _pagesView;

    /// <summary>Initialises a new <see cref="TextLayer"/>.</summary>
    /// <param name="documentId">
    /// Identity of the document the text was extracted from - the registry's content-hash identity,
    /// so that a text layer carried in run state can be proven to belong to the document being
    /// validated rather than assumed to.
    /// </param>
    /// <param name="pagesInOrder">
    /// The text of each page, in document order: the first element is logical page
    /// <see cref="FirstLogicalPageNumber"/>. Elements may be empty; none may be
    /// <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="pagesInOrder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="documentId"/> is empty or white space, <paramref name="pagesInOrder"/> is
    /// empty, or any element of it is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <b>An empty page list is refused.</b> A document the parser reported as having no pages has no
    /// text layer, and admitting one would make the grounding gate pass vacuously - every snippet
    /// check would be skipped rather than failed, which is precisely backwards for a gate whose job
    /// is to catch invention.
    /// </remarks>
    public TextLayer(string documentId, IEnumerable<string> pagesInOrder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(pagesInOrder);

        // Materialised once, up front: the argument may be a lazily-evaluated sequence, and
        // validating one enumeration while storing another would validate something other than what
        // is stored.
        string[] pages = [.. pagesInOrder];

        if (pages.Length == 0)
        {
            throw new ArgumentException(
                "A text layer must cover at least one page; a document the parser reported as having no pages has no text layer to ground against.",
                nameof(pagesInOrder));
        }

        for (int index = 0; index < pages.Length; index++)
        {
            if (pages[index] is null)
            {
                throw new ArgumentException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The text of logical page {index + FirstLogicalPageNumber} is null. A page with no text is an empty string; null would make 'no text' indistinguishable from 'not extracted'."),
                    nameof(pagesInOrder));
            }
        }

        DocumentId = documentId;
        _pages = pages;
        _pagesView = new ReadOnlyCollection<string>(pages);
    }

    /// <summary>Gets the content-hash identity of the document this text was extracted from.</summary>
    public string DocumentId { get; }

    /// <summary>Gets the number of pages in the document, every one of which has a text string.</summary>
    public int PageCount => _pages.Length;

    /// <summary>
    /// Gets the text of every page in document order, where index 0 is logical page
    /// <see cref="FirstLogicalPageNumber"/>.
    /// </summary>
    /// <remarks>
    /// Wrapped rather than handed out as the backing array: an <see cref="IReadOnlyList{T}"/> that a
    /// caller can cast back to <c>string[]</c> and write through is read-only by politeness only, and
    /// this is evidence a published value is audited against. The wrapper is built once in the
    /// constructor rather than per access - the text layer is immutable, so a fresh view per call
    /// would allocate on every read for no difference in behaviour, and the grounding gate reads this
    /// once per extracted field.
    /// </remarks>
    public IReadOnlyList<string> Pages => _pagesView;

    /// <summary>Gets the text of one page, by the page number a reader sees.</summary>
    /// <param name="logicalPageNumber">The 1-based logical page number.</param>
    /// <returns>The page's text, which may be empty.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="logicalPageNumber"/> is below <see cref="FirstLogicalPageNumber"/> or above
    /// <see cref="PageCount"/>.
    /// </exception>
    /// <remarks>
    /// Throwing is right for a caller that knows the page exists. A validator checking a page number
    /// an agent reported does not know that - an agent may cite page 9 of a two-page document - and
    /// should use <see cref="TryGetPage"/>, which turns that into a gate failure rather than an
    /// exception thrown through the workflow.
    /// </remarks>
    public string GetPage(int logicalPageNumber)
    {
        if (!TryGetPage(logicalPageNumber, out string? text))
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalPageNumber),
                logicalPageNumber,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The document has pages {FirstLogicalPageNumber} to {_pages.Length + FirstLogicalPageNumber - 1}."));
        }

        return text;
    }

    /// <summary>Gets the text of one page if that page exists.</summary>
    /// <param name="logicalPageNumber">The 1-based logical page number.</param>
    /// <param name="text">The page's text, which may be empty, or <see langword="null"/> if there is no such page.</param>
    /// <returns><see langword="true"/> if the document has that page.</returns>
    public bool TryGetPage(int logicalPageNumber, [NotNullWhen(true)] out string? text)
    {
        int index = logicalPageNumber - FirstLogicalPageNumber;

        if (index < 0 || index >= _pages.Length)
        {
            text = null;
            return false;
        }

        text = _pages[index];
        return true;
    }
}
