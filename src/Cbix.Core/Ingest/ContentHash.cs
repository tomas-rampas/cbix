using System.Security.Cryptography;

namespace Cbix.Core.Ingest;

/// <summary>
/// A cryptographic digest of a document's bytes, in the canonical form CBIX uses as document identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the identity of a document in CBIX.</b> Ingest hashes the bytes it actually read and
/// uses <see cref="Canonical"/> verbatim as <c>DocumentReference.DocumentId</c> - the memoisation
/// key for document preparation, the join key for provenance, and the primary key of
/// <c>dbo.document_registry</c>. Deriving identity from content is what makes re-submission
/// idempotent (design 8) and what stops a submitter from choosing an identity that already belongs
/// to someone else's document (see the trust note on <c>DocumentReference</c>'s constructor).
/// </para>
/// <para>
/// <b>Why the algorithm travels with the digest.</b> A bare hex string is ambiguous the day the
/// digest changes: two algorithms produce different strings for the same bytes, and nothing in the
/// stored value says which one ran. Carrying the algorithm makes an identity self-describing, so a
/// future migration is a data migration with a decidable answer rather than an archaeology exercise
/// across a table of anonymous hex.
/// </para>
/// <para>
/// <b>Case is part of the contract.</b> <see cref="Value"/> is lower-case hex, always. The canonical
/// form is a database key and a cache key; a value that differed only in case would be the same
/// document under a case-insensitive SQL Server collation and a different one in a .NET dictionary,
/// which is precisely the kind of split identity a content-addressed design exists to prevent.
/// </para>
/// </remarks>
public sealed record ContentHash
{
    /// <summary>The only digest algorithm CBIX currently computes, as it appears in <see cref="Canonical"/>.</summary>
    public const string Sha256Algorithm = "sha256";

    /// <summary>Length in hex characters of a SHA-256 digest.</summary>
    private const int Sha256HexLength = 64;

    /// <summary>Initialises a new <see cref="ContentHash"/> from an already-computed digest.</summary>
    /// <param name="algorithm">
    /// Digest algorithm name in lower case. Only <see cref="Sha256Algorithm"/> is accepted: a name
    /// this type cannot itself compute would be an identity nothing can re-derive from the bytes,
    /// so it is rejected rather than stored.
    /// </param>
    /// <param name="value">The digest as lower-case hexadecimal, of the length the algorithm produces.</param>
    /// <exception cref="ArgumentException">
    /// Either argument is empty or white space; <paramref name="algorithm"/> is not a supported
    /// algorithm; or <paramref name="value"/> is not lower-case hex of the expected length.
    /// </exception>
    public ContentHash(string algorithm, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!string.Equals(algorithm, Sha256Algorithm, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{algorithm}' is not a supported content-hash algorithm; CBIX computes '{Sha256Algorithm}'.",
                nameof(algorithm));
        }

        ValidateLowerCaseHex(value, Sha256HexLength);

        Algorithm = algorithm;
        Value = value;
        Canonical = $"{algorithm}:{value}";
    }

    /// <summary>Gets the digest algorithm name, in lower case.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the digest itself, as lower-case hexadecimal.</summary>
    public string Value { get; }

    /// <summary>
    /// Gets the canonical <c>algorithm:hex</c> rendering. This exact string is the document's
    /// registry identity and the value stored in <c>document_registry.document_id</c>.
    /// </summary>
    public string Canonical { get; }

    /// <summary>Computes the SHA-256 content hash of <paramref name="content"/>.</summary>
    /// <param name="content">The document bytes.</param>
    /// <returns>The content hash of those bytes.</returns>
    public static ContentHash ComputeSha256(ReadOnlySpan<byte> content)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(content, digest);

        return new ContentHash(Sha256Algorithm, Convert.ToHexStringLower(digest));
    }

    /// <summary>Wraps an already-computed SHA-256 digest.</summary>
    /// <param name="digest">The 32-byte digest.</param>
    /// <returns>The content hash carrying that digest.</returns>
    /// <exception cref="ArgumentException"><paramref name="digest"/> is not <see cref="SHA256.HashSizeInBytes"/> long.</exception>
    /// <remarks>
    /// The entry point for callers that must hash incrementally - a bounded, counting read, for
    /// instance - and would otherwise have to format the hex themselves and get the casing right.
    /// </remarks>
    public static ContentHash FromSha256Digest(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException(
                $"A {Sha256Algorithm} digest is {SHA256.HashSizeInBytes} bytes; this one is {digest.Length}.",
                nameof(digest));
        }

        return new ContentHash(Sha256Algorithm, Convert.ToHexStringLower(digest));
    }

    /// <summary>
    /// Computes the SHA-256 content hash of a stream, reading it once from the beginning without
    /// buffering the whole document in memory.
    /// </summary>
    /// <param name="content">A readable stream over the document bytes.</param>
    /// <param name="cancellationToken">Token that cancels the read.</param>
    /// <returns>The content hash of the bytes read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="content"/> is not readable.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// <b>The whole stream, or nothing.</b> A seekable stream is rewound here rather than left to the
    /// caller's discipline: hashing from wherever the position happened to be returns a well-formed
    /// digest of a suffix of the document - an identity that looks entirely normal, deduplicates
    /// against nothing, and is wrong. A non-seekable stream cannot be rewound, and cannot even be
    /// asked where it is (<see cref="Stream.Position"/> throws when <see cref="Stream.CanSeek"/> is
    /// <see langword="false"/>), so for that case alone the precondition remains the caller's: supply
    /// an unread stream. Ingest always supplies a seekable <see cref="FileStream"/>.
    /// </remarks>
    public static async Task<ContentHash> ComputeSha256Async(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(content));
        }

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        byte[] digest = await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false);

        return FromSha256Digest(digest);
    }

    /// <summary>Returns the canonical <c>algorithm:hex</c> rendering.</summary>
    /// <returns>The value of <see cref="Canonical"/>.</returns>
    public override string ToString() => Canonical;

    /// <summary>
    /// Fails closed on the digest: exactly <paramref name="expectedLength"/> characters, each one
    /// lower-case hex.
    /// </summary>
    /// <remarks>
    /// Upper-case input is rejected rather than normalised. Normalising would accept two spellings
    /// of the same identity into a system whose whole point is that identity is derived, and would
    /// hide the caller that produced the wrong spelling; rejecting surfaces it at the boundary.
    /// </remarks>
    private static void ValidateLowerCaseHex(string value, int expectedLength)
    {
        if (value.Length != expectedLength)
        {
            throw new ArgumentException(
                $"A {Sha256Algorithm} digest must be {expectedLength} hex characters; '{value}' has {value.Length}.",
                nameof(value));
        }

        foreach (char character in value)
        {
            bool isLowerHex = character is (>= '0' and <= '9') or (>= 'a' and <= 'f');
            if (!isLowerHex)
            {
                throw new ArgumentException(
                    $"A content hash must be lower-case hexadecimal; '{value}' contains '{character}'.",
                    nameof(value));
            }
        }
    }
}
