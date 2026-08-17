namespace Cbix.Core.Workflows;

/// <summary>
/// Where triage's routing edge sits: the confidence at or above which a document proceeds to
/// extraction rather than to a human (design 5.3, design 9 "New/unknown layout family").
/// </summary>
/// <remarks>
/// <para>
/// <b>A configured number rather than a constant, because it is not a fact about the code.</b>
/// Design 11 says plainly that model-reported confidence is not to be trusted at face value and that
/// thresholds are calibrated against observed correction rates. Nobody has those rates yet - the
/// golden set is at v0 and the review flywheel has not turned once - so the default below is an
/// opening position, and the whole point of it being configurable is that the first pilot batch will
/// move it without a build.
/// </para>
/// <para>
/// <b>Both directions of getting it wrong cost something, which is why it is stated rather than
/// tuned quietly.</b> Too low and unrecognised documents proceed into extraction, where seven agents
/// are paid for and the wrong answers look exactly like right ones until the validator or a reviewer
/// catches them. Too high and every document queues for a human, which is a pipeline that has
/// automated nothing. The default leans toward the second: an over-eager review queue is visible and
/// annoying, while an under-eager one is invisible and wrong.
/// </para>
/// </remarks>
public sealed class TriageRoutingOptions
{
    /// <summary>
    /// The default routing threshold: 0.7.
    /// </summary>
    /// <remarks>
    /// Chosen, not measured, and it should be read that way. It is comfortably below the confidence a
    /// model reports for a document it genuinely recognises (the golden-set specimens sit near 0.95)
    /// and comfortably above what it reports for a document it is guessing at, which is the only
    /// property an uncalibrated threshold can honestly claim. The eval harness (Sprint 02) is what
    /// replaces this number with one backed by correction rates.
    /// </remarks>
    public const double DefaultReviewConfidenceThreshold = 0.7d;

    /// <summary>Initialises a new <see cref="TriageRoutingOptions"/>.</summary>
    /// <param name="reviewConfidenceThreshold">
    /// The confidence at or above which extraction proceeds, in the unit interval. Defaults to
    /// <see cref="DefaultReviewConfidenceThreshold"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reviewConfidenceThreshold"/> is not a finite number in the unit interval. A
    /// threshold above 1 would route every document to review no matter how certain triage was, and a
    /// negative one would route none - both are configurations that silently disable a gate, which is
    /// the class of mistake that must fail at startup rather than at the first document.
    /// </exception>
    public TriageRoutingOptions(double reviewConfidenceThreshold = DefaultReviewConfidenceThreshold)
    {
        if (!double.IsFinite(reviewConfidenceThreshold)
            || reviewConfidenceThreshold < 0d
            || reviewConfidenceThreshold > 1d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reviewConfidenceThreshold),
                reviewConfidenceThreshold,
                "The triage routing threshold must be a finite value in the unit interval: confidence is "
                    + "reported on that scale, so a threshold outside it disables the review gate in one "
                    + "direction or the other without anything reporting that it had.");
        }

        ReviewConfidenceThreshold = reviewConfidenceThreshold;
    }

    /// <summary>Gets the confidence at or above which a document proceeds to extraction.</summary>
    public double ReviewConfidenceThreshold { get; }
}
