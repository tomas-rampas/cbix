using System.Text.Json;

using Cbix.Core.Documents;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// The handle's whole reason to exist is surviving the serializer MAF checkpointing uses, so the
/// round trip is the test that matters. <c>DocumentContent</c> cannot do this - its content blocks
/// carry provider payloads in <c>AIContent.RawRepresentation</c>, which is <c>[JsonIgnore]</c> and
/// comes back null - which is exactly why the handle is a separate, BCL-only type.
/// </summary>
public sealed class DocumentContentHandleTests
{
    private static readonly Uri SpecimenLocation =
        new("file:///data/Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf");

    [Fact]
    public void SystemTextJson_RoundTrip_PreservesEveryField()
    {
        DocumentContentHandle original = new("sha256:abc", "claude-native-pdf", "file_0123456789");

        string json = JsonSerializer.Serialize(original);
        DocumentContentHandle? restored = JsonSerializer.Deserialize<DocumentContentHandle>(json);

        Assert.NotNull(restored);
        Assert.Equal(original.DocumentId, restored.DocumentId);
        Assert.Equal(original.ProfileName, restored.ProfileName);
        Assert.Equal(original.ProviderToken, restored.ProviderToken);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void SystemTextJson_RoundTrip_PreservesAbsentProviderToken()
    {
        // A profile with nothing to resume from stores null; that must survive too, rather than
        // coming back as an empty string a resume would try to redeem.
        DocumentContentHandle original = new("sha256:abc", "text-only");

        DocumentContentHandle? restored =
            JsonSerializer.Deserialize<DocumentContentHandle>(JsonSerializer.Serialize(original));

        Assert.NotNull(restored);
        Assert.Null(restored.ProviderToken);
        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankDocumentId_Throws(string documentId)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentContentHandle(documentId, "text-only"));

        Assert.Equal("documentId", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankProfileName_Throws(string profileName)
    {
        // A handle whose profile is unknown cannot be redeemed: the resume would not know which
        // implementation understands the token.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentContentHandle("sha256:abc", profileName));

        Assert.Equal("profileName", error.ParamName);
    }

    [Fact]
    public void IsRedeemableBy_SameDocumentAndIssuingProfile_IsRedeemable()
    {
        DocumentReference document = new("sha256:abc", SpecimenLocation, "DE.pdf");
        DocumentContentHandle handle = new("sha256:abc", "claude-native-pdf", "file_0123");

        Assert.True(handle.IsRedeemableBy(document, "claude-native-pdf"));
    }

    [Fact]
    public void IsRedeemableBy_DifferentProfile_IsNotRedeemableButDoesNotThrow()
    {
        // The expected state after a provider swap: the token means nothing to this profile, so the
        // caller re-prepares and issues a new handle. An operational case, not an error.
        DocumentReference document = new("sha256:abc", SpecimenLocation, "DE.pdf");
        DocumentContentHandle handle = new("sha256:abc", "claude-native-pdf", "file_0123");

        Assert.False(handle.IsRedeemableBy(document, "generic-vision"));
    }

    [Fact]
    public void IsRedeemableBy_DifferentDocument_Throws()
    {
        // Never recoverable: redeeming this would prepare one document's content under another
        // document's identity - cache poisoning plus mis-attributed provenance.
        DocumentReference otherDocument = new("sha256:def", SpecimenLocation, "CH.pdf");
        DocumentContentHandle handle = new("sha256:abc", "claude-native-pdf", "file_0123");

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => handle.IsRedeemableBy(otherDocument, "claude-native-pdf"));

        Assert.Equal("document", error.ParamName);
    }

    [Fact]
    public void IsRedeemableBy_DifferentDocumentAndDifferentProfile_ThrowsRatherThanReportingNotRedeemable()
    {
        // Order matters: the document check must win, otherwise the unrecoverable case hides behind
        // the recoverable one and the caller silently re-prepares the wrong document.
        DocumentReference otherDocument = new("sha256:def", SpecimenLocation, "CH.pdf");
        DocumentContentHandle handle = new("sha256:abc", "claude-native-pdf", "file_0123");

        Assert.Throws<ArgumentException>(() => handle.IsRedeemableBy(otherDocument, "generic-vision"));
    }

    [Fact]
    public void IsRedeemableBy_NullDocument_Throws()
    {
        DocumentContentHandle handle = new("sha256:abc", "text-only");

        Assert.Throws<ArgumentNullException>(() => handle.IsRedeemableBy(null!, "text-only"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsRedeemableBy_BlankProfileName_Throws(string profileName)
    {
        DocumentReference document = new("sha256:abc", SpecimenLocation, "DE.pdf");
        DocumentContentHandle handle = new("sha256:abc", "text-only");

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => handle.IsRedeemableBy(document, profileName));

        Assert.Equal("profileName", error.ParamName);
    }

    [Fact]
    public void Equality_IsByValue()
    {
        DocumentContentHandle first = new("sha256:abc", "claude-native-pdf", "file_0123");
        DocumentContentHandle second = new("sha256:abc", "claude-native-pdf", "file_0123");
        DocumentContentHandle other = new("sha256:abc", "claude-native-pdf", "file_9999");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }
}
