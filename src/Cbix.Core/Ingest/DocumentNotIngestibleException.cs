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
        : this(reason, documentPath, byteLength, innerException: null)
    {
    }

    /// <summary>
    /// Initialises a new <see cref="DocumentNotIngestibleException"/> that carries the underlying
    /// failure it was raised from.
    /// </summary>
    /// <param name="reason">Why the submission cannot be treated as a document.</param>
    /// <param name="documentPath">The fully resolved path of the submission.</param>
    /// <param name="byteLength">Bytes read before the refusal, or <see langword="null"/> when the refusal happened before reading.</param>
    /// <param name="innerException">
    /// The failure this refusal was raised from, or <see langword="null"/> when the refusal was
    /// decided outright. Used by <see cref="DocumentNotIngestibleReason.Unreadable"/>, where the PDF
    /// parser's own exception is the only description of <em>how</em> the file is damaged and
    /// discarding it would leave an operator with nothing to act on.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="reason"/> is not a defined value, or <paramref name="documentPath"/> is empty
    /// or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteLength"/> is negative.</exception>
    /// <remarks>
    /// <para>
    /// <b>The inner exception is typed as <see cref="Exception"/> and that is load-bearing.</b> It
    /// lets the parser's diagnostic survive without letting the parser's exception hierarchy into
    /// anybody's <c>catch</c> clause: no caller can bind to a PDF-library type through this property,
    /// so replacing the parser stays a non-breaking change - which is the whole point of routing the
    /// failure through <see cref="ITextLayerExtractor"/>'s documented refusal in the first place.
    /// </para>
    /// <para>
    /// <b>The refusal's own <see cref="Exception.Message"/> stays path-free and reason-derived</b>, as
    /// for every other reason. An inner exception's message is not held to that: it is the library's
    /// own text about the file's structure. Anything that renders an exception chain into a log
    /// should assume the inner message is unsanitised - which is why the structured event emitted at
    /// the point of refusal records the exception's <em>type</em> and a sanitised path, never its
    /// message.
    /// </para>
    /// </remarks>
    public DocumentNotIngestibleException(
        DocumentNotIngestibleReason reason,
        string documentPath,
        long? byteLength,
        Exception? innerException)
        : base(DescribeSafely(reason, documentPath, byteLength), innerException)
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
            DocumentNotIngestibleReason.Unreadable => "The submitted document could not be read as a PDF.",
            DocumentNotIngestibleReason.UnsupportedPageNumbering =>
                "The submitted document numbers its own pages, so its printed page numbers cannot be trusted to match its physical ones.",
            _ => "The submitted document was refused by the ingest gate.",
        };
    }
}
