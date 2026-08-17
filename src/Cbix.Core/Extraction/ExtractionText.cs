using System.Globalization;

namespace Cbix.Core.Extraction;

/// <summary>
/// The bounds and the rendering rule every agent-reply parser at this boundary shares.
/// </summary>
/// <remarks>
/// <para>
/// <b>One place, because the reasoning is one reasoning.</b> Story S01-14 established these on the
/// triage parser and S01-16 needs them identically for the section parsers - and Sprint 02 adds six
/// more. Two copies of a security bound is how one of them stops being updated; two copies of a
/// sanitiser is how one of them gets a subtly different category list.
/// </para>
/// <para>
/// <b>It is the only copy, and that took a correction to become true.</b> S01-16 introduced this type
/// with a sharing claim in its remarks while <see cref="DocumentProfileParser"/> kept byte-identical
/// private copies of the sanitiser, the fence normalisation and both bounds - so the codebase briefly
/// held THREE sanitisers and a comment asserting one. Both parsers now delegate here, and
/// <c>DocumentProfileParser</c>'s public constants are aliases of these, so its published surface is
/// unchanged and there is exactly one body to get right.
/// </para>
/// <para>
/// <b>Public, deliberately.</b> Everything that RENDERS model-supplied text must use the same
/// treatment, and that set is wider than this assembly: the BDD harness's grounding report quotes
/// snippets back, and Sprint 02's <c>ValidationReport</c> will quote them into retry prompts and
/// review rows. An internal helper would have each of those growing its own near-copy, which is the
/// failure this type exists to end rather than to repeat one layer out.
/// </para>
/// <para>
/// <b>These are the BOUNDARY's bounds, not a provider's.</b> An adapter's output-token setting is a
/// fact about one vendor, and the agnosticism constraint makes the vendor replaceable - so a limit
/// that lived there would have to be re-argued for every provider and would simply be absent behind
/// a stub, a streaming response, or a vendor that ignored the setting. A boundary check belongs at
/// the boundary, where it holds regardless of who is upstream.
/// </para>
/// </remarks>
public static class ExtractionText
{
    /// <summary>
    /// The longest reply any parser here will read at all, in characters (256 Ki).
    /// </summary>
    /// <remarks>
    /// <b>Measured:</b> a 40 MB reply drove roughly 214 MB of allocation through
    /// <c>JsonDocument.Parse</c> before any contract rule was consulted - the string, the UTF-8
    /// transcode and the document's own metadata, none of which a later refusal un-allocates.
    /// Refusing on length costs one integer comparison. 256 Ki is roughly three orders of magnitude
    /// above the largest legitimate section reply, so it is a bound against absurdity rather than a
    /// limit anything real approaches.
    /// </remarks>
    public const int MaxReplyLength = 256 * 1024;

    /// <summary>
    /// The longest value any parser here will accept in a contract string field.
    /// </summary>
    /// <remarks>
    /// The fields these parsers read are document types, ISO country codes, references, version
    /// designations, owners and verbatim snippets - things copied out of a document's own front
    /// matter, none longer than a line or two. 200 characters is generous for every one of them and
    /// far below anything that could be a paragraph, so a value past it is a model that pasted a page
    /// into a field. It also keeps every value comfortably inside the columns design 6 sizes for
    /// published text.
    /// </remarks>
    public const int MaxFieldLength = 200;

    /// <summary>
    /// The longest verbatim snippet a provenance envelope may carry.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="MaxFieldLength"/> on purpose: a snippet is a quotation providing context
    /// for a value, so it is legitimately longer than the value it evidences - a table row, a
    /// sentence. It is still bounded, because <c>field_provenance.source_snippet</c> in design 6 is
    /// <c>nvarchar(1000)</c> and a snippet that will not fit there is one the audit trail cannot keep.
    /// </remarks>
    public const int MaxSnippetLength = 1000;

    /// <summary>
    /// The shortest verbatim snippet that can be evidence of anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, and the arithmetic is the whole justification.</b> A snippet is checked by ordinal
    /// containment in a page's text (design 5.6), so its evidential weight falls off a cliff as it
    /// gets shorter: a single character is contained in essentially every page of every document, so
    /// <c>"e"</c> "grounds" against a document nobody read. Any floor above 1 removes that whole class.
    /// </para>
    /// <para>
    /// <b>Why 2 and not more.</b> The floor cannot exceed the shortest legitimate quotation the
    /// contracts admit, and that is 2: the prompt requires a snippet to contain the value it evidences,
    /// and the shortest value any contract declares is an ISO 3166-1 alpha-2 country code
    /// (<c>DE</c>, <c>CH</c>). A floor of 3 or 4 would look stronger and would refuse a correct
    /// extraction the first time a model quoted a country code tightly - a false refusal on real data,
    /// which is a worse failure than weak evidence because it stops a working pipeline.
    /// </para>
    /// <para>
    /// <b>The residual is stated rather than hidden:</b> a 2-character snippet is weak evidence and
    /// this floor does not pretend otherwise. What actually measures provenance quality is the
    /// validator's per-field containment plus the golden set (design 5.9), and the eval harness is what
    /// may raise this number once correction rates exist - the same footing as the triage routing
    /// threshold.
    /// </para>
    /// </remarks>
    public const int MinSnippetLength = 2;

    /// <summary>
    /// Removes a Markdown code fence around an otherwise complete reply.
    /// </summary>
    /// <param name="trimmed">The reply, already trimmed of surrounding white space.</param>
    /// <returns>The payload inside the fence, or <paramref name="trimmed"/> unchanged.</returns>
    /// <remarks>
    /// <para>
    /// <b>Total and deterministic, which is what makes it transport handling rather than leniency.</b>
    /// The reply either opens with <c>```</c> and closes with <c>```</c>, in which case the fence and
    /// its optional language tag are packaging several providers emit regardless of instructions, or it
    /// does not, in which case nothing happens.
    /// </para>
    /// <para>
    /// <b>It never scans for JSON embedded in surrounding text.</b> That is a search, and a search that
    /// succeeds on the wrong substring produces a confident answer nobody wrote - worse than the
    /// refusal it replaced. A reply with an explanatory sentence before the object is refused.
    /// </para>
    /// </remarks>
    public static string Unfence(string trimmed)
    {
        ArgumentNullException.ThrowIfNull(trimmed);

        const string Fence = "```";

        if (!trimmed.StartsWith(Fence, StringComparison.Ordinal)
            || !trimmed.EndsWith(Fence, StringComparison.Ordinal)
            || trimmed.Length < Fence.Length * 2)
        {
            return trimmed;
        }

        // The opening fence may carry a language tag ("```json"), which runs to the end of its line.
        int firstNewline = trimmed.IndexOf('\n', Fence.Length);

        return firstNewline < 0 ? trimmed : trimmed[(firstNewline + 1)..^Fence.Length].Trim();
    }

    /// <summary>
    /// Renders a model-supplied fragment safely inside a message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same reasoning as <c>PathBoundary.ForLog</c> applied to the other untrusted string class
    /// this process handles. A model's reply is text from outside the estate: control characters and
    /// line separators forge log lines, format characters (U+200E and friends) reorder rendered text,
    /// and an unpaired surrogate can break the encoder in whichever sink receives it. The value is
    /// replaced rather than dropped, because an operator needs to see that something was there.
    /// </para>
    /// <para>
    /// Bounded before rendering, because a model that returned a megabyte of one character would
    /// otherwise put it in a log line, a review-queue row and an operator's terminal.
    /// </para>
    /// </remarks>
    /// <param name="value">The model-supplied fragment.</param>
    /// <returns>A rendering safe to place in a message, a log line or a stored row.</returns>
    public static string ForMessage(string value)
    {
        const int MaxRenderedLength = 80;
        const string TruncationMarker = "...[truncated]";

        ReadOnlySpan<char> kept = value.Length <= MaxRenderedLength
            ? value
            : value.AsSpan(0, MaxRenderedLength);

        string sanitised = string.Create(kept.Length, value, static (destination, original) =>
        {
            for (int index = 0; index < destination.Length; index++)
            {
                char character = original[index];

                destination[index] = char.IsControl(character)
                    || char.GetUnicodeCategory(character)
                        is UnicodeCategory.Format
                        or UnicodeCategory.Surrogate
                        or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator
                    ? '�'
                    : character;
            }
        });

        // Appended after sanitisation, so nothing in the input can forge the marker.
        return value.Length <= MaxRenderedLength ? sanitised : sanitised + TruncationMarker;
    }
}
