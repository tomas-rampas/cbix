using Cbix.Core.Documents;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// Story S01-06. The render density is the one thing about a page render this pipeline has an
/// opinion about, so its bounds are where that opinion is enforced.
/// </summary>
public sealed class PageImageRenderOptionsTests
{
    [Fact]
    public void Default_IsTheDocumentedMatrixLegibilityDensity()
    {
        // Pinned rather than merely read back. The default is a decision about whether Sprint 02's
        // Matrix agent can read 8-10pt table cells, and a change to it is a change to matrix cell
        // accuracy - the PoC's headline metric - so it should not move without someone editing this
        // line and noticing why it is here. 150 DPI puts an A4 page at 1240 x 1754.
        Assert.Equal(150, PageImageRenderOptions.DefaultDpi);
        Assert.Equal(150, new PageImageRenderOptions().Dpi);
    }

    [Theory]
    [InlineData(PageImageRenderOptions.MinimumDpi)]
    [InlineData(200)]
    [InlineData(PageImageRenderOptions.MaximumDpi)]
    public void Constructor_WithinBounds_IsAccepted(int dpi) =>
        Assert.Equal(dpi, new PageImageRenderOptions(dpi).Dpi);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PageImageRenderOptions.MinimumDpi - 1)]
    public void Constructor_BelowTheFloor_Throws(int dpi) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageImageRenderOptions(dpi));

    [Theory]
    [InlineData(PageImageRenderOptions.MaximumDpi + 1)]
    [InlineData(20_000)]
    public void Constructor_AboveTheCeiling_Throws(int dpi)
    {
        // The ceiling is a resource bound, not a taste. Pixel count grows with the square of DPI,
        // so a mistyped configuration value on a service that renders whatever documents arrive is
        // a memory-exhaustion vector; this makes it a startup failure naming the configuration.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageImageRenderOptions(dpi));
    }

    [Fact]
    public void Defaults_AreTheDocumentedResourceCeilings()
    {
        // Pinned like the DPI default above, and for a stronger reason: these two are the only
        // bounds standing between an attacker-supplied page geometry and the allocator. A change to
        // either should be a line someone edits here on purpose.
        PageImageRenderOptions options = new();

        Assert.Equal(500, options.MaxPageCount);
        Assert.Equal(80, options.MaxPageMegapixels);
        Assert.Equal(80_000_000L, options.MaxPagePixels);
    }

    [Fact]
    public void MaxPagePixels_IsComputedInSixtyFourBits()
    {
        // The value this is compared against is an attacker-influenced geometry multiplied by a DPI
        // ratio. At the maximum configurable ceiling the product overflows a 32-bit int, and a bound
        // that overflows is a bound that is defeated rather than exceeded.
        PageImageRenderOptions options = new(maxPageMegapixels: PageImageRenderOptions.MaximumMaxPageMegapixels);

        Assert.Equal(1_000_000_000L, options.MaxPagePixels);

        // The property is a long, and that is the assertion: a 30000 x 30000 page - the measured
        // bomb, and well inside what the PDF format permits - is 900 million pixels, and the square
        // of a page only a little larger passes int.MaxValue. Computing this product in 32 bits
        // would wrap it negative and sail under any ceiling.
        Assert.IsType<long>(options.MaxPagePixels);

        const long BombPixels = 30_000L * 30_000L;
        Assert.True(BombPixels > options.MaxPagePixels / 2, "The measured bomb should be the same order as the maximum ceiling.");
        Assert.True(50_000L * 50_000L > int.MaxValue, "A page only modestly larger than the bomb overflows 32-bit arithmetic.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PageImageRenderOptions.MaximumMaxPageCount + 1)]
    public void Constructor_PageCountCeilingOutsideItsRange_Throws(int maxPageCount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageImageRenderOptions(maxPageCount: maxPageCount));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PageImageRenderOptions.MaximumMaxPageMegapixels + 1)]
    public void Constructor_MegapixelCeilingOutsideItsRange_Throws(int maxPageMegapixels) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageImageRenderOptions(maxPageMegapixels: maxPageMegapixels));

    [Fact]
    public void Default_AdmitsEveryLegitimatePageThisPipelineRenders()
    {
        // Guards the ceiling against being set so tight it refuses real work. The arithmetic on
        // DefaultMaxPageMegapixels claims A3 at the maximum DPI - the largest legitimate combination
        // this configuration can express - fits; that claim is checked here rather than trusted.
        PageImageRenderOptions options = new(dpi: PageImageRenderOptions.MaximumDpi);

        const double A3WidthPoints = 842;
        const double A3HeightPoints = 1191;
        double scale = PageImageRenderOptions.MaximumDpi / 72.0;
        double pixels = Math.Ceiling(A3WidthPoints * scale) * Math.Ceiling(A3HeightPoints * scale);

        Assert.True(
            pixels <= options.MaxPagePixels,
            $"A3 at {PageImageRenderOptions.MaximumDpi} DPI is {pixels / 1_000_000:F1} MP, above the default ceiling of {options.MaxPageMegapixels} MP.");
    }
}
