using Cbix.Core.Documents;
using Cbix.Core.Ingest;

namespace Cbix.UnitTests.Documents;

/// <summary>Story S01-06. What a rendered page is allowed to be.</summary>
public sealed class PageImageTests
{
    [Fact]
    public void Constructor_KeepsWhatItWasGiven()
    {
        byte[] bytes = [1, 2, 3];

        PageImage image = new(3, "image/png", bytes);

        Assert.Equal(3, image.LogicalPageNumber);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(3, image.Content.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_PageNumberBelowTheFirstLogicalPage_Throws(int logicalPageNumber)
    {
        // Page numbering is 1-based because that is what a reader sees in a viewer, which is the
        // numbering the extraction prompts mandate and therefore the numbering an agent reports
        // back. A 0-based slip here would shift the whole document by one page.
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageImage(logicalPageNumber, "image/png", new byte[] { 1 }));
        Assert.Equal(1, TextLayer.FirstLogicalPageNumber);
    }

    [Fact]
    public void Constructor_EmptyContent_Throws()
    {
        // A blank page still encodes to a valid white image, so zero bytes is not "the page was
        // blank" - it is a render that failed quietly, and admitting one would hand the model a
        // block it cannot decode while the run reports full visual fidelity.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new PageImage(1, "image/png", ReadOnlyMemory<byte>.Empty));

        Assert.Equal("content", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankMediaType_Throws(string mediaType) =>
        Assert.Throws<ArgumentException>(() => new PageImage(1, mediaType, new byte[] { 1 }));

    [Fact]
    public void Constructor_NullMediaType_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new PageImage(1, null!, new byte[] { 1 }));
}
