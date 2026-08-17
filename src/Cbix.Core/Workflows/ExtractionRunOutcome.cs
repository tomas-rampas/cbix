using Cbix.Core.Documents;

namespace Cbix.Core.Workflows;

/// <summary>
/// The workflow's terminal value: what one run did to one document.
/// </summary>
/// <remarks>
/// <para>
/// <b>A run needs an observable end, and this is it.</b> A graph whose last executor simply stops is
/// indistinguishable from one that stalled halfway, and "the run completes to the persist step" is
/// the assertion S01-09's agnosticism proof is written against. Yielding a typed outcome makes
/// completion a fact a caller reads rather than an absence it infers.
/// </para>
/// <para>
/// <b>Every terminal branch yields one, and that is the invariant worth stating.</b> The graph has
/// two ways to finish - extraction reaching persist, and a duplicate stopping at the registry - and
/// both end in an <see cref="ExtractionRunOutcome"/> carrying its <see cref="Disposition"/>. An
/// earlier shape let the duplicate branch end with no output at all, so a caller had to read "no
/// output event" as "duplicate", which is the same signal a crashed run gives. One output per run,
/// always, with the disposition on it.
/// </para>
/// <para>
/// <b>Persistence itself is Sprint 03.</b> Staging tables, the transactional publish, effective
/// dating on <c>(doc_ref, doc_version)</c> and the closing of superseded validity windows all land
/// there. This records what a run reached; it does not claim anything was written.
/// </para>
/// </remarks>
public sealed class ExtractionRunOutcome
{
    /// <summary>Initialises a new <see cref="ExtractionRunOutcome"/>.</summary>
    /// <param name="document">The document the run processed.</param>
    /// <param name="disposition">How the run ended.</param>
    /// <param name="sectionCount">How many section candidates reached the persist step.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sectionCount"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// A duplicate-ignored run reports extracted sections. Its run stopped at the registry, so there
    /// was nothing to extract from and nothing to count - a non-zero count there would mean the
    /// short-circuit had failed silently and something had already been paid for.
    /// </exception>
    public ExtractionRunOutcome(
        DocumentReference document,
        ExtractionRunDisposition disposition,
        int sectionCount)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(sectionCount);

        if (disposition == ExtractionRunDisposition.DuplicateIgnored && sectionCount != 0)
        {
            throw new ArgumentException(
                $"A duplicate submission cannot report {sectionCount} extracted sections: its run stops at "
                    + "the registry, so no section agent ran for it.",
                nameof(sectionCount));
        }

        // The same reasoning one branch further along, and worth its own check rather than being folded
        // into the one above: a document routed to review was routed there INSTEAD of into extraction,
        // so a non-zero count means the routing edge fired and the section agents ran anyway - the
        // document queued for a human and paid for twice, reported as if the gate had worked.
        if (disposition == ExtractionRunDisposition.ReviewQueued && sectionCount != 0)
        {
            throw new ArgumentException(
                $"A document queued for review cannot report {sectionCount} extracted sections: the "
                    + "routing edge sends it to a human instead of to the section agents, so a non-zero "
                    + "count means both ran.",
                nameof(sectionCount));
        }

        Document = document;
        Disposition = disposition;
        SectionCount = sectionCount;
    }

    /// <summary>Gets the document the run processed.</summary>
    public DocumentReference Document { get; }

    /// <summary>Gets how the run ended.</summary>
    public ExtractionRunDisposition Disposition { get; }

    /// <summary>Gets the number of section candidates that reached the persist step.</summary>
    public int SectionCount { get; }
}
