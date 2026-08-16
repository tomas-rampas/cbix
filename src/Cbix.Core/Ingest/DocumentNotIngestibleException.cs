namespace Cbix.Core.Ingest;

/// <summary>
/// Thrown when a submission lies inside the ingest root but is not something the pipeline can treat
/// as a document.
/// </summary>
/// <remarks>
/// <para>
/// These outcomes are named rather than left to whichever validation happened to notice first. An
/// empty file previously surfaced as an <see cref="ArgumentOutOfRangeException"/> naming a
/// <c>byteLength</c> parameter the caller never passed, which tells an operator nothing about their
/// file and points a developer at the wrong layer.
/// </para>
/// <para>
/// Like <see cref="IngestRootViolationException"/>, the message is derived from
/// <see cref="Reason"/> and carries no paths: the submitted path is input somebody else chose, and
/// it belongs in a structured log field, not in message text. <see cref="DocumentPath"/> and
/// <see cref="ByteLength"/> carry the detail.
/// </para>
/// </remarks>
public sealed class DocumentNotIngestibleException : Exception
{
    /// <summary>Initialises a new <see cref="DocumentNotIngestibleException"/>.</summary>
    /// <param name="reason">Why the submission cannot be treated as a document.</param>
    /// <param name="documentPath">The fully resolved path of the submission.</param>
    /// <param name="byteLength">
    /// Bytes read before the refusal, or <see langword="null"/> when the refusal happened before
    /// reading. For <see cref="DocumentNotIngestibleReason.TooLarge"/> this is the point the read was
    /// abandoned, not the file's full size, which is deliberately never measured.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="reason"/> is not a defined value, or <paramref name="documentPath"/> is empty
    /// or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteLength"/> is negative.</exception>
    public DocumentNotIngestibleException(DocumentNotIngestibleReason reason, string documentPath, long? byteLength = null)
        : base(DescribeSafely(reason, documentPath, byteLength))
    {
        Reason = reason;
        DocumentPath = documentPath;
        ByteLength = byteLength;
    }

    /// <summary>Gets why the submission cannot be treated as a document.</summary>
    public DocumentNotIngestibleReason Reason { get; }

    /// <summary>Gets the fully resolved path of the submission.</summary>
    public string DocumentPath { get; }

    /// <summary>Gets the number of bytes read before the refusal, if any were.</summary>
    public long? ByteLength { get; }

    /// <summary>Validates the arguments, then returns a constant message for the reason.</summary>
    /// <remarks>See the note on <see cref="IngestRootViolationException"/>: validation runs inside the
    /// <c>base(...)</c> argument so it happens before the message exists, not after.</remarks>
    private static string DescribeSafely(DocumentNotIngestibleReason reason, string documentPath, long? byteLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);

        if (byteLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), byteLength, "A byte count cannot be negative.");
        }

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentException($"'{reason}' is not a defined document refusal reason.", nameof(reason));
        }

        return reason switch
        {
            DocumentNotIngestibleReason.NotAFile => "The submitted path is a directory, not a document.",
            DocumentNotIngestibleReason.Empty => "The submitted document is empty.",
            DocumentNotIngestibleReason.TooLarge => "The submitted document exceeds the configured maximum document size.",
            _ => "The submitted document was refused by the ingest gate.",
        };
    }
}
