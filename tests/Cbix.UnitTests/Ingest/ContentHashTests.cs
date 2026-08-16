using System.Security.Cryptography;
using System.Text;

using Cbix.Core.Ingest;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Covers the content hash: the type that turns bytes into the identity every later stage keys on.
/// Determinism and canonical form are not cosmetic here - they are what makes re-submission
/// idempotent and what stops one document being registered twice under two spellings of one key.
/// </summary>
public sealed class ContentHashTests
{
    /// <summary>NIST's SHA-256 answer for the ASCII string "abc", the standard known-answer vector.</summary>
    private const string AbcDigest = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void ComputeSha256_MatchesTheKnownAnswerVector()
    {
        // Anchoring on a published vector rather than on the type's own output is what makes the
        // rest of these tests meaningful: it proves the algorithm, not merely self-consistency.
        ContentHash hash = ContentHash.ComputeSha256(Encoding.ASCII.GetBytes("abc"));

        Assert.Equal(AbcDigest, hash.Value);
    }

    [Fact]
    public void ComputeSha256_IsDeterministicAcrossCalls()
    {
        byte[] content = Encoding.UTF8.GetBytes("Cross-Border Trading Legal Instruction");

        Assert.Equal(ContentHash.ComputeSha256(content), ContentHash.ComputeSha256(content));
    }

    [Fact]
    public void ComputeSha256_OneFlippedByte_ChangesTheHash()
    {
        byte[] original = [0x25, 0x50, 0x44, 0x46];
        byte[] altered = [0x25, 0x50, 0x44, 0x47];

        Assert.NotEqual(ContentHash.ComputeSha256(original), ContentHash.ComputeSha256(altered));
    }

    [Fact]
    public async Task ComputeSha256Async_AgreesWithTheSpanOverload()
    {
        byte[] content = Encoding.UTF8.GetBytes("DE specimen bytes");
        using MemoryStream stream = new(content);

        ContentHash streamed = await ContentHash.ComputeSha256Async(stream);

        Assert.Equal(ContentHash.ComputeSha256(content), streamed);
    }

    [Fact]
    public void Canonical_IsAlgorithmColonHex()
    {
        ContentHash hash = ContentHash.ComputeSha256(Encoding.ASCII.GetBytes("abc"));

        Assert.Equal($"sha256:{AbcDigest}", hash.Canonical);
        Assert.Equal(hash.Canonical, hash.ToString());
    }

    [Fact]
    public void Constructor_UpperCaseHex_Throws()
    {
        // Rejected rather than normalised: two spellings of one identity would be one row under a
        // case-insensitive SQL collation and two entries in a .NET dictionary.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new ContentHash("sha256", AbcDigest.ToUpperInvariant()));

        Assert.Equal("value", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ba7816bf")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015adff")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a!")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a ")]
    public void Constructor_MalformedDigest_Throws(string value)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => new ContentHash("sha256", value));

        Assert.Equal("value", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SHA256")]
    [InlineData("md5")]
    [InlineData("sha512")]
    public void Constructor_UnsupportedAlgorithm_Throws(string algorithm)
    {
        // Including sha512: an algorithm this type cannot compute is an identity nothing can
        // re-derive from the bytes, so it is refused at the boundary rather than stored.
        ArgumentException error = Assert.Throws<ArgumentException>(() => new ContentHash(algorithm, AbcDigest));

        Assert.Equal("algorithm", error.ParamName);
    }

    [Fact]
    public async Task ComputeSha256Async_AlreadyAdvancedStream_HashesTheWholeDocument()
    {
        // A digest of a suffix is a well-formed identity of the wrong bytes: it deduplicates against
        // nothing and looks entirely normal in the registry. A seekable stream is therefore rewound
        // here rather than trusted to arrive at position zero.
        byte[] content = Encoding.UTF8.GetBytes("cross-border trading legal instruction");
        using MemoryStream stream = new(content);
        stream.Position = 10;

        ContentHash hash = await ContentHash.ComputeSha256Async(stream);

        Assert.Equal(ContentHash.ComputeSha256(content), hash);
    }

    [Fact]
    public void FromSha256Digest_MatchesTheSpanOverload()
    {
        byte[] content = Encoding.ASCII.GetBytes("abc");

        Assert.Equal(ContentHash.ComputeSha256(content), ContentHash.FromSha256Digest(SHA256.HashData(content)));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void FromSha256Digest_WrongDigestLength_Throws(int length)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => ContentHash.FromSha256Digest(new byte[length]));

        Assert.Equal("digest", error.ParamName);
    }

    [Fact]
    public async Task ComputeSha256Async_NullStream_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => ContentHash.ComputeSha256Async(null!));
    }

    [Fact]
    public async Task ComputeSha256Async_UnreadableStream_Throws()
    {
        MemoryStream stream = new([1, 2, 3]);
        await stream.DisposeAsync();

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => ContentHash.ComputeSha256Async(stream));

        Assert.Equal("content", error.ParamName);
    }

    [Fact]
    public async Task ComputeSha256Async_CancelledToken_Throws()
    {
        using MemoryStream stream = new(RandomNumberGenerator.GetBytes(1024));
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ContentHash.ComputeSha256Async(stream, cancellation.Token));
    }
}
