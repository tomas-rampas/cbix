using Cbix.Core.Documents;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// Covers the capability descriptor's construction rules. The BDD scenario for story S01-04
/// proves the flag exists and is provider-neutral; these tests pin down what the flag is
/// allowed to say, which is where the degraded-mode guarantee of S01-07 actually lives.
/// </summary>
public sealed class DocumentContentCapabilitiesTests
{
    [Fact]
    public void Constructor_WithoutVisualContentAndNotDegraded_Throws()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentContentCapabilities("text-only", includesVisualContent: false, isDegraded: false));

        Assert.Equal("isDegraded", error.ParamName);
    }

    [Fact]
    public void Constructor_TextOnlyProfile_IsAcceptedWhenFlaggedDegraded()
    {
        DocumentContentCapabilities capabilities =
            new("text-only", includesVisualContent: false, isDegraded: true);

        Assert.False(capabilities.IncludesVisualContent);
        Assert.True(capabilities.IsDegraded);
    }

    [Fact]
    public void Constructor_VisualProfile_MayStillReportDegraded()
    {
        // Visual presentation is not automatically full fidelity - a profile that renders pages
        // locally may still consider itself below the native-PDF baseline. Only the reverse
        // direction is constrained.
        DocumentContentCapabilities capabilities =
            new("generic-vision", includesVisualContent: true, isDegraded: true);

        Assert.True(capabilities.IncludesVisualContent);
        Assert.True(capabilities.IsDegraded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankProfileName_Throws(string profileName)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentContentCapabilities(profileName, includesVisualContent: true, isDegraded: false));

        Assert.Equal("profileName", error.ParamName);
    }

    [Fact]
    public void Constructor_NullProfileName_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DocumentContentCapabilities(null!, includesVisualContent: true, isDegraded: false));
    }

    [Fact]
    public void Equality_IsByValue()
    {
        // The hand-written constructor must not cost the record its value semantics: the eval
        // harness compares and groups capability descriptors across runs.
        DocumentContentCapabilities first = new("claude-native-pdf", includesVisualContent: true, isDegraded: false);
        DocumentContentCapabilities second = new("claude-native-pdf", includesVisualContent: true, isDegraded: false);
        DocumentContentCapabilities other = new("generic-vision", includesVisualContent: true, isDegraded: false);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, other);
    }
}
