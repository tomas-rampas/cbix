namespace Cbix.Core.Documents;

/// <summary>
/// Thrown when the page rasteriser itself fails, as distinct from the document being one that
/// cannot be rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate type from the ingest refusal family.</b>
/// <see cref="Ingest.DocumentNotIngestibleException"/> is a statement about a document, and its
/// defining property is that it is deterministic in that document's bytes - the same file refused
/// today is refused identically tomorrow, which is what lets ingest treat a re-submission as a
/// duplicate rather than as something worth retrying. A fault inside a native rendering engine is
/// not that. It says something about the renderer, the host or the moment, and filing it as a
/// document refusal makes two separate mistakes at once: it tells an operator that a supplier sent
/// a bad file, and it hides the class of event that most wants watching.
/// </para>
/// <para>
/// <b>What most wants watching.</b> Rasterisation runs untrusted PDFs through PDFium, which is
/// Chromium-derived native code (design 11 addendum). Memory-safety defects there are routine Chrome
/// patch content and are invisible to the NuGet audit gate, because they are filed upstream against
/// PDFium rather than against the NuGet package id. A managed exception surfacing out of that
/// boundary - a null handle where a bitmap was expected, a Skia allocation failure, an error code
/// the wrapper could not name - is the observable, survivable end of the same spectrum whose other
/// end is an access violation that no <c>catch</c> block sees at all. It is a security-relevant
/// operational signal, and it is emitted as a structured event at the point it is raised for
/// exactly that reason.
/// </para>
/// <para>
/// <b>It is not transient, but it is not a document refusal either.</b> A caller must not spend
/// retries on it: if PDFium cannot render a page once, the identical call will not render it
/// moments later, and a retry loop against a faulting native library is how one hostile document
/// becomes sustained pressure on the host. The run routes to review, and the event routes to
/// whoever watches the renderer.
/// </para>
/// </remarks>
public sealed class PageRenderFaultException : Exception
{
    /// <summary>Initialises a new <see cref="PageRenderFaultException"/>.</summary>
    /// <param name="documentPath">The fully resolved path of the document being rendered.</param>
    /// <param name="detail">What the renderer was doing when it faulted, safe for operational logs.</param>
    /// <param name="innerException">The exception that crossed the native boundary, if there was one.</param>
    public PageRenderFaultException(string documentPath, string detail, Exception? innerException = null)
        : base($"The page rasteriser faulted while rendering a document: {detail}.", innerException)
    {
        DocumentPath = documentPath;
    }

    /// <summary>Gets the fully resolved path of the document that was being rendered.</summary>
    /// <remarks>
    /// Carried separately from the message so a handler can act on it without parsing prose, and
    /// kept out of the message itself so that a path is never rendered into a log line by accident.
    /// The renderer sanitises it through the ingest path-logging boundary before emitting it, the
    /// same treatment ingest refusals get; a caller that logs it elsewhere owes it the same.
    /// </remarks>
    public string DocumentPath { get; }
}
