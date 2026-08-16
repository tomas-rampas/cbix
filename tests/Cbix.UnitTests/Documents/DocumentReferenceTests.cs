using Cbix.Core.Documents;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// Covers the document reference's construction rules. The reference reaches file-system and
/// upload code, so this is a trust-boundary type: the tests below are the proof that it fails
/// closed on shape, alongside the containment check the registry owns.
/// </summary>
public sealed class DocumentReferenceTests
{
    private static readonly Uri SpecimenLocation =
        new("file:///data/Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf");

    [Fact]
    public void Constructor_DefaultsMediaTypeToPdf()
    {
        DocumentReference reference = new("sha256:abc", SpecimenLocation, "DE_SPECIMEN.pdf");

        Assert.Equal("application/pdf", reference.MediaType);
    }

    [Fact]
    public void Constructor_LocalFileLocation_IsAccepted()
    {
        DocumentReference reference = new("sha256:abc", SpecimenLocation, "DE_SPECIMEN.pdf");

        Assert.Equal(Uri.UriSchemeFile, reference.Location.Scheme);
    }

    [Fact]
    public void Constructor_RelativeLocation_Throws()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentReference("sha256:abc", new Uri("data/DE.pdf", UriKind.Relative), "DE.pdf"));

        Assert.Equal("location", error.ParamName);
    }

    [Theory]
    [InlineData("http://evil.example/DE.pdf")]
    [InlineData("https://evil.example/DE.pdf")]
    [InlineData("ftp://evil.example/DE.pdf")]
    [InlineData("jar:file:///data/DE.zip!/DE.pdf")]
    public void Constructor_NonFileScheme_Throws(string location)
    {
        // Fail closed: ingest reads a local share, so anything that would make the pipeline fetch
        // over the network on a submitter's say-so is rejected until a profile actually needs it.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentReference("sha256:abc", new Uri(location), "DE.pdf"));

        Assert.Equal("location", error.ParamName);
    }

    [Fact]
    public void Constructor_UncLocation_Throws()
    {
        // file://server/share is an SMB fetch: on Windows it can be coerced into leaking NTLM
        // credentials to whichever host the URI names.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentReference("sha256:abc", new Uri("file://attacker.example/share/DE.pdf"), "DE.pdf"));

        Assert.Equal("location", error.ParamName);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\config\\sam")]
    [InlineData("sub/dir/DE.pdf")]
    [InlineData("sub\\dir\\DE.pdf")]
    [InlineData("..")]
    [InlineData("DE.pdf:stream")]
    [InlineData("C:DE.pdf")]
    [InlineData("DE\".pdf")]
    [InlineData("DE\r\nX-Injected: 1.pdf")]
    [InlineData("CON")]
    [InlineData("con.pdf")]
    [InlineData("NUL.pdf")]
    [InlineData("COM1.pdf")]
    [InlineData("LPT9.pdf")]
    public void Constructor_FileNameThatIsNotABareDisplayName_Throws(string fileName)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentReference("sha256:abc", SpecimenLocation, fileName));

        Assert.Equal("fileName", error.ParamName);
    }

    [Fact]
    public void Constructor_QuoteInFileName_IsRejectedBeforeItCanBreakAHeader()
    {
        // A quote is how a display name breaks out of a Content-Disposition filename="..." field.
        DocumentReference valid = new("sha256:abc", SpecimenLocation, "DE_SPECIMEN.pdf");
        Assert.Equal("DE_SPECIMEN.pdf", valid.FileName);

        Assert.Throws<ArgumentException>(
            () => new DocumentReference("sha256:abc", SpecimenLocation, "DE\"; name=\"evil.pdf"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_BlankDocumentId_Throws(string documentId)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentReference(documentId, SpecimenLocation, "DE.pdf"));

        Assert.Equal("documentId", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_BlankFileName_Throws(string fileName)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentReference("sha256:abc", SpecimenLocation, fileName));

        Assert.Equal("fileName", error.ParamName);
    }

    [Fact]
    public void Constructor_NullLocation_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DocumentReference("sha256:abc", null!, "DE.pdf"));
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("application/pdf; charset=utf-8")]
    [InlineData("image/png")]
    public void Constructor_WellFormedMediaType_IsAccepted(string mediaType)
    {
        // Grammar validation, not an allowlist: the corpus may legitimately grow past PDF.
        DocumentReference reference = new("sha256:abc", SpecimenLocation, "DE.pdf", mediaType);

        Assert.Equal(mediaType, reference.MediaType);
    }

    [Theory]
    [InlineData("application/pdf\r\nX-Injected: 1")]
    [InlineData("not-a-media-type")]
    [InlineData("application/")]
    [InlineData("/pdf")]
    public void Constructor_MalformedMediaType_Throws(string mediaType)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentReference("sha256:abc", SpecimenLocation, "DE.pdf", mediaType));

        Assert.Equal("mediaType", error.ParamName);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        // This documents the record's value semantics only. It is NOT the memoisation contract:
        // a provider's memo key is DocumentId alone, per IDocumentContentProvider.PrepareAsync.
        DocumentReference first = new("sha256:abc", SpecimenLocation, "DE.pdf");
        DocumentReference second = new("sha256:abc", SpecimenLocation, "DE.pdf");
        DocumentReference other = new("sha256:def", SpecimenLocation, "DE.pdf");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }
}
