using Cbix.Core.Ingest;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Covers the text layer's invariants: 1-based logical page numbering, contiguity by construction,
/// and the refusals that keep the grounding gate from being handed something it would pass
/// vacuously.
/// </summary>
public sealed class TextLayerTests
{
    private const string DocumentId = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Constructor_PagesInOrder_AreNumberedFromOne()
    {
        TextLayer layer = new(DocumentId, ["first", "second", "third"]);

        Assert.Equal(3, layer.PageCount);
        Assert.Equal(1, TextLayer.FirstLogicalPageNumber);
        Assert.Equal("first", layer.GetPage(1));
        Assert.Equal("second", layer.GetPage(2));
        Assert.Equal("third", layer.GetPage(3));
        Assert.Equal(["first", "second", "third"], layer.Pages);
        Assert.Equal(DocumentId, layer.DocumentId);
    }

    [Fact]
    public void TryGetPage_PageOutsideTheDocument_IsFalseRatherThanAThrow()
    {
        // The validator checks a page number a model reported, and a model may cite page 9 of a
        // two-page document. That has to become a gate failure, not an exception thrown through the
        // workflow.
        TextLayer layer = new(DocumentId, ["first", "second"]);

        Assert.False(layer.TryGetPage(0, out string? beforeFirst));
        Assert.Null(beforeFirst);

        Assert.False(layer.TryGetPage(3, out string? pastLast));
        Assert.Null(pastLast);

        Assert.True(layer.TryGetPage(2, out string? last));
        Assert.Equal("second", last);
    }

    [Fact]
    public void GetPage_PageOutsideTheDocument_Throws()
    {
        TextLayer layer = new(DocumentId, ["only page"]);

        Assert.Throws<ArgumentOutOfRangeException>(() => layer.GetPage(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.GetPage(2));
    }

    [Fact]
    public void Constructor_PageWithNoText_IsAnEmptyStringAndCounts()
    {
        // A purely graphical page genuinely has no text. Recording it as an empty string keeps "a
        // string for every page" true and makes grounding fail closed on that page.
        TextLayer layer = new(DocumentId, ["first", string.Empty, "third"]);

        Assert.Equal(3, layer.PageCount);
        Assert.Equal(string.Empty, layer.GetPage(2));
    }

    [Fact]
    public void Constructor_NoPages_IsRefused()
    {
        // An empty text layer would make every grounding check skip rather than fail - exactly
        // backwards for a gate whose job is catching invention.
        ArgumentException error = Assert.Throws<ArgumentException>(() => new TextLayer(DocumentId, []));

        Assert.Equal("pagesInOrder", error.ParamName);
    }

    [Fact]
    public void Constructor_NullPageText_IsRefusedAndNamesThePage()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new TextLayer(DocumentId, ["first", null!, "third"]));

        Assert.Equal("pagesInOrder", error.ParamName);
        Assert.Contains("page 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_MissingIdentity_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new TextLayer(null!, ["page"]));
        Assert.Throws<ArgumentException>(() => new TextLayer("   ", ["page"]));
        Assert.Throws<ArgumentNullException>(() => new TextLayer(DocumentId, null!));
    }

    [Fact]
    public void Constructor_LazySequence_IsMaterialisedOnce()
    {
        // A sequence validated on one enumeration and stored on another would validate something
        // other than what it stores; this proves the source is walked exactly once.
        int enumerations = 0;

        TextLayer layer = new(DocumentId, Pages());

        Assert.Equal(1, enumerations);
        Assert.Equal(2, layer.PageCount);
        Assert.Equal(1, enumerations);

        IEnumerable<string> Pages()
        {
            enumerations++;
            yield return "first";
            yield return "second";
        }
    }

    [Fact]
    public void Pages_CannotBeWrittenThroughByCastingBackToAnArray()
    {
        // The text layer is evidence a published value is audited against; a read-only view a caller
        // can cast back to string[] and mutate is read-only by politeness only.
        TextLayer layer = new(DocumentId, ["first"]);

        Assert.IsNotType<string[]>(layer.Pages);
    }
}
