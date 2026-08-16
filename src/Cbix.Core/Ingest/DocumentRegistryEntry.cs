using Cbix.Core.Documents;

namespace Cbix.Core.Ingest;

/// <summary>
/// One row of the document registry: the record that a specific set of document bytes entered the
/// pipeline, and the identity everything downstream keys on.
/// </summary>
/// <remarks>
/// <para>
/// This is the in-process shape of <c>dbo.document_registry</c> (design 6, operational tables;
/// DDL in <c>db/schema/document_registry.sql</c>). It is deliberately narrow: the registry answers
/// "have these exact bytes been seen, and under what identity", nothing else. Extraction results,
/// text layers and provider handles belong to their own tables and their own stories.
/// </para>
/// <para>
/// <b>Identity is derived, never supplied.</b> There is no <c>documentId</c> parameter here:
/// <see cref="DocumentId"/> comes from <see cref="ContentHash"/>, and the constructor rejects a
/// <see cref="Document"/> whose <see cref="DocumentReference.DocumentId"/> disagrees. That closes,
/// structurally rather than by convention, the failure <c>DocumentReference</c>'s constructor
/// warns about: an identity chosen by whoever submitted the document turns memoisation into cache
/// poisoning and mis-attributes provenance.
/// </para>
/// <para>
/// <b>Registry rows are written once.</b> A duplicate submission does not update the row - it is a
/// no-op plus an audit entry (design 9, "Duplicate submission"). <see cref="FirstSeenUtc"/>
/// therefore means the first time these bytes were seen and keeps that meaning forever; the
/// history of later submissions lives in the append-only ingest audit log
/// (<see cref="IIngestAuditLog"/>), where it does not compete with the registry's job.
/// </para>
/// </remarks>
public sealed record DocumentRegistryEntry
{
    /// <summary>Largest <c>file_name</c> the registry can store, matching <c>nvarchar(260)</c>.</summary>
    public const int MaxFileNameLength = 260;

    /// <summary>Largest <c>media_type</c> the registry can store, matching <c>varchar(255)</c>.</summary>
    public const int MaxMediaTypeLength = 255;

    /// <summary>Largest <c>source_location</c> the registry can store, matching <c>nvarchar(1000)</c>.</summary>
    public const int MaxLocationLength = 1000;

    /// <summary>Initialises a new <see cref="DocumentRegistryEntry"/>.</summary>
    /// <param name="contentHash">Hash of the bytes the ingest step actually read. Supplies the entry's identity.</param>
    /// <param name="document">
    /// Reference to the document as it was read: its location (already resolved and
    /// containment-checked against the ingest root), display file name and media type. Its
    /// <see cref="DocumentReference.DocumentId"/> must equal <paramref name="contentHash"/>'s
    /// canonical form.
    /// </param>
    /// <param name="byteLength">Length in bytes of the hashed content. Must be greater than zero: a zero-byte submission is not a document.</param>
    /// <param name="firstSeenUtc">When these bytes were first registered. Must be a UTC instant.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contentHash"/> or <paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteLength"/> is not greater than zero.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="document"/>'s identity does not match <paramref name="contentHash"/>, or
    /// <paramref name="firstSeenUtc"/> is not UTC.
    /// </exception>
    public DocumentRegistryEntry(
        ContentHash contentHash,
        DocumentReference document,
        long byteLength,
        DateTimeOffset firstSeenUtc)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);

        if (!string.Equals(document.DocumentId, contentHash.Canonical, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The document identity '{document.DocumentId}' is not derived from the content hash '{contentHash.Canonical}'. "
                    + "The registry is the sole authority for identity and derives it from the bytes it read.",
                nameof(document));
        }

        // The storable bounds are enforced here rather than discovered on INSERT. A value that
        // exceeds a column is either a truncation (a provenance record quietly naming the wrong
        // location) or a failed transaction at the end of a run that has already paid for its model
        // calls; both are worse than refusing at construction, and both would be found by the
        // database rather than by a test.
        RejectOverlongValue(document.FileName, MaxFileNameLength, "file name");
        RejectOverlongValue(document.MediaType, MaxMediaTypeLength, "media type");
        RejectOverlongValue(document.Location.OriginalString, MaxLocationLength, "source location");

        if (firstSeenUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"The first-seen timestamp must be UTC; its offset is {firstSeenUtc.Offset}.",
                nameof(firstSeenUtc));
        }

        ContentHash = contentHash;
        Document = document;
        ByteLength = byteLength;
        FirstSeenUtc = firstSeenUtc;
    }

    /// <summary>Gets the hash of the registered bytes.</summary>
    public ContentHash ContentHash { get; }

    /// <summary>Gets the reference to the document as it was read at registration time.</summary>
    public DocumentReference Document { get; }

    /// <summary>Gets the length in bytes of the registered content.</summary>
    public long ByteLength { get; }

    /// <summary>Gets the instant these bytes were first registered, in UTC.</summary>
    public DateTimeOffset FirstSeenUtc { get; }

    /// <summary>
    /// Gets the registry identity of the document - the canonical form of <see cref="ContentHash"/>,
    /// and the primary key of the registry row.
    /// </summary>
    public string DocumentId => ContentHash.Canonical;

    /// <summary>Refuses a value the registry's schema could not store without truncating it.</summary>
    /// <remarks>
    /// The parameter name is the literal "document" because every value checked here is reached
    /// through that constructor argument; a <c>nameof</c> is not available from a static helper.
    /// </remarks>
    private static void RejectOverlongValue(string value, int maximumLength, string description)
    {
        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The document's {description} is {value.Length} characters; the registry stores at most {maximumLength}.",
                "document");
        }
    }
}
