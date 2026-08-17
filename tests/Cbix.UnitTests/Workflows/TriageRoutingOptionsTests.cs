using Cbix.Core.Workflows;

namespace Cbix.UnitTests.Workflows;

/// <summary>
/// Where triage's routing edge sits (story S01-15).
/// </summary>
public sealed class TriageRoutingOptionsTests
{
    [Fact]
    public void Default_IsTheDocumentedOpeningPosition()
    {
        // Pinned so a change to the number is a change to a test rather than a quiet edit. It is an
        // opening position, not a measurement - design 11 says thresholds are calibrated against
        // observed correction rates, and there are none yet - so what this protects is that moving it
        // is deliberate.
        Assert.Equal(0.7d, new TriageRoutingOptions().ReviewConfidenceThreshold);
        Assert.Equal(TriageRoutingOptions.DefaultReviewConfidenceThreshold, new TriageRoutingOptions().ReviewConfidenceThreshold);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(0.5d)]
    [InlineData(1d)]
    public void Constructor_AcceptsTheWholeUnitInterval(double threshold) =>
        Assert.Equal(threshold, new TriageRoutingOptions(threshold).ReviewConfidenceThreshold);

    [Theory]
    [InlineData(-0.1d)]
    [InlineData(1.1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_RefusesAThresholdThatWouldDisableTheGate(double threshold)
    {
        // Both directions silently disable a gate: above 1 routes every document to review no matter
        // how certain triage was, and below 0 routes none. NaN is worse than either - every comparison
        // against it is false, so a document would match NEITHER edge and the run would go quiet.
        // Failing at composition is what keeps these out of a deployment.
        Assert.Throws<ArgumentOutOfRangeException>(() => new TriageRoutingOptions(threshold));
    }
}
