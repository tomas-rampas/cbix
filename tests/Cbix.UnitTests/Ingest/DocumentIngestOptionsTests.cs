using Cbix.Core.Ingest;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Covers the ingest configuration's guards. These run at construction on purpose: a root that can
/// never contain anything is a deployment mistake, and discovering it per document means discovering
/// it after a read, after a hash, with a message about the wrong thing.
/// </summary>
public sealed class DocumentIngestOptionsTests
{
    private static readonly string LocalRoot = OperatingSystem.IsWindows() ? @"C:\ingest" : "/srv/ingest";

    [Fact]
    public void Constructor_LocalRoot_IsAccepted()
    {
        DocumentIngestOptions options = new(LocalRoot, DocumentIngestOptions.ClaudeFilesApiLimitBytes);

        Assert.Equal(LocalRoot, options.IngestRoot);
        Assert.Equal(32L * 1024 * 1024, options.MaxDocumentBytes);
    }

    [Theory]
    [InlineData(@"\\fileserver\ingest")]
    [InlineData(@"//fileserver/ingest")]
    [InlineData(@"\\?\C:\ingest")]
    [InlineData(@"\\.\C:\ingest")]
    public void Constructor_UncOrDeviceRoot_Throws(string ingestRoot)
    {
        // Path.IsPathFullyQualified returns true for all four, so the earlier guard admitted them and
        // the failure surfaced per document, after the read and hash, naming the document rather than
        // the configuration. A UNC root additionally means every containment check performs outbound
        // SMB authentication.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentIngestOptions(ingestRoot, 1024));

        Assert.Equal("ingestRoot", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/ingest")]
    public void Constructor_RootThatIsNotFullyQualified_Throws(string ingestRoot)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentIngestOptions(ingestRoot, 1024));

        Assert.Equal("ingestRoot", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveMaximumSize_Throws(long maxDocumentBytes)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DocumentIngestOptions(LocalRoot, maxDocumentBytes));

        Assert.Equal("maxDocumentBytes", error.ParamName);
    }

    [Fact]
    public void ClaudeFilesApiLimit_MatchesTheDesignDocumentsFigure()
    {
        // Design 5.1 quotes the API's per-request PDF limit as 32 MB. It is offered as a reference
        // point, never as a default: a size bound nobody chose is a bound nobody owns.
        Assert.Equal(33_554_432, DocumentIngestOptions.ClaudeFilesApiLimitBytes);
    }
}
