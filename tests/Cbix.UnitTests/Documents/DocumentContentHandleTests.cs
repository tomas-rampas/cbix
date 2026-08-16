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
    public void Equality_IsByValue()
    {
        DocumentContentHandle first = new("sha256:abc", "claude-native-pdf", "file_0123");
        DocumentContentHandle second = new("sha256:abc", "claude-native-pdf", "file_0123");
        DocumentContentHandle other = new("sha256:abc", "claude-native-pdf", "file_9999");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }
}
