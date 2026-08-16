using System.Text;

using Cbix.Core.Documents;
using Cbix.Core.Ingest;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Covers the registry row's construction rules. The rule that matters most is the one with no
/// parameter: identity is derived from the content hash, never supplied, so a submitter cannot name
/// a document into somebody else's identity.
/// </summary>
public sealed class DocumentRegistryEntryTests
{
    private static readonly ContentHash SpecimenHash = ContentHash.ComputeSha256(Encoding.UTF8.GetBytes("DE specimen"));

    private static readonly DateTimeOffset FirstSeen = new(2026, 8, 16, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void DocumentId_IsTheCanonicalContentHash()
    {
        DocumentRegistryEntry entry = new(SpecimenHash, Reference(SpecimenHash.Canonical), 4096, FirstSeen);

        Assert.Equal(SpecimenHash.Canonical, entry.DocumentId);
        Assert.Equal(SpecimenHash, entry.ContentHash);
    }

    [Fact]
    public void Constructor_DocumentIdentityNotDerivedFromTheHash_Throws()
    {
        // The exact cache-poisoning / mis-attributed-provenance case DocumentReference's own
        // constructor documentation warns about, closed here structurally.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentRegistryEntry(SpecimenHash, Reference("sha256:deadbeef"), 4096, FirstSeen));

        Assert.Equal("document", error.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveByteLength_Throws(long byteLength)
    {
        // Mirrors ck_document_registry_byte_length in db/schema/document_registry.sql: an empty
        // submission is not a document, and its "hash of nothing" identity never reaches the table.
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DocumentRegistryEntry(SpecimenHash, Reference(SpecimenHash.Canonical), byteLength, FirstSeen));

        Assert.Equal("byteLength", error.ParamName);
    }

    [Fact]
    public void Constructor_NonUtcTimestamp_Throws()
    {
        DateTimeOffset localTime = new(2026, 8, 16, 9, 30, 0, TimeSpan.FromHours(2));

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentRegistryEntry(SpecimenHash, Reference(SpecimenHash.Canonical), 4096, localTime));

        Assert.Equal("firstSeenUtc", error.ParamName);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DocumentRegistryEntry(null!, Reference(SpecimenHash.Canonical), 4096, FirstSeen));
        Assert.Throws<ArgumentNullException>(
            () => new DocumentRegistryEntry(SpecimenHash, null!, 4096, FirstSeen));
    }

    private static DocumentReference Reference(string documentId) =>
        new(documentId, new Uri("file:///ingest/DE_SPECIMEN.pdf"), "DE_SPECIMEN.pdf");
}
