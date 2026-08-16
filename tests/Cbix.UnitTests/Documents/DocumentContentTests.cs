using System.Text.Json;

using Cbix.Core.Documents;

using Microsoft.Extensions.AI;

namespace Cbix.UnitTests.Documents;

/// <summary>
/// Covers the prepared-content container: it must refuse to represent "nothing prepared" as a
/// successful result, must not share mutable state with its caller, and must let a profile smuggle
/// a provider-specific payload through <see cref="AIContent.RawRepresentation"/> without that type
/// reaching the port's signature.
/// </summary>
public sealed class DocumentContentTests
{
    private const string ProfileName = "claude-native-pdf";

    private static readonly DocumentContentCapabilities FullFidelity =
        new(ProfileName, includesVisualContent: true, isDegraded: false);

    private static readonly DocumentContentHandle Handle =
        new("sha256:abc", ProfileName, "file_0123");

    [Fact]
    public void Constructor_EmptyContent_Throws()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentContent([], FullFidelity, Handle));

        Assert.Equal("content", error.ParamName);
    }

    [Fact]
    public void Constructor_NullContent_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DocumentContent(null!, FullFidelity, Handle));
    }

    [Fact]
    public void Constructor_NullElementInContent_Throws()
    {
        AIContent[] withHole = [new TextContent("page 1"), null!];

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentContent(withHole, FullFidelity, Handle));

        Assert.Equal("content", error.ParamName);
    }

    [Fact]
    public void Constructor_NullCapabilities_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DocumentContent([new TextContent("page 1")], null!, Handle));
    }

    [Fact]
    public void Constructor_NullHandle_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DocumentContent([new TextContent("page 1")], FullFidelity, null!));
    }

    [Fact]
    public void Constructor_HandleFromADifferentProfile_Throws()
    {
        // A resume redeems the handle against the profile named in it; a mismatch would checkpoint
        // a handle no profile can honour.
        DocumentContentHandle foreignHandle = new("sha256:abc", "generic-vision", "render-cache-1");

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentContent([new TextContent("page 1")], FullFidelity, foreignHandle));

        Assert.Equal("handle", error.ParamName);
    }

    [Fact]
    public void Constructor_CopiesContentDefensively()
    {
        List<AIContent> mutable = [new TextContent("page 1")];

        DocumentContent prepared = new(mutable, FullFidelity, Handle);
        mutable.Add(new TextContent("page 2"));
        mutable[0] = new TextContent("tampered");

        Assert.Single(prepared.Content);
        Assert.Equal("page 1", Assert.IsType<TextContent>(prepared.Content[0]).Text);
    }

    [Fact]
    public void Content_PreservesOrderAndRawRepresentation()
    {
        // RawRepresentation is the escape hatch that keeps provider payloads out of Core's
        // signatures: the profile puts its native block here, and Core never inspects it.
        object nativeDocumentBlock = new { file_id = "file_0123", cache_control = "ephemeral" };
        TextContent instructions = new("Use only the supplied document.");
        DataContent page = new(new byte[] { 1, 2, 3 }, "image/png") { RawRepresentation = nativeDocumentBlock };

        DocumentContent prepared = new([instructions, page], FullFidelity, Handle);

        Assert.Equal(2, prepared.Content.Count);
        Assert.Same(instructions, prepared.Content[0]);
        Assert.Same(nativeDocumentBlock, prepared.Content[1].RawRepresentation);
    }

    [Fact]
    public void RawRepresentation_DoesNotSurviveJsonRoundTrip_WhichIsWhyTheHandleExists()
    {
        // This is the measured basis for "DocumentContent is in-process only, never a checkpoint
        // payload". If this test ever starts failing, the port's persistence rules can be revisited
        // - until then, run state persists DocumentContent.Handle instead.
        DataContent page = new(new byte[] { 1, 2, 3 }, "application/pdf")
        {
            RawRepresentation = new { file_id = "file_0123" },
        };

        AIContent? restored = JsonSerializer.Deserialize<AIContent>(JsonSerializer.Serialize<AIContent>(page));

        Assert.NotNull(restored);
        Assert.Null(restored.RawRepresentation);
    }
}
