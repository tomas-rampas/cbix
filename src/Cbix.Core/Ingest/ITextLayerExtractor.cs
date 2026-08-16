using Cbix.Core.Documents;

namespace Cbix.Core.Ingest;

/// <summary>
/// Produces the local, per-page text layer of a registered document (design 5.1).
/// </summary>
/// <remarks>
/// <para>
/// A port rather than a direct call into a PDF library, for two reasons that have nothing to do with
/// ceremony. First, the text layer is what the grounding gate checks against, so the ingest service
/// has to be testable against a text layer a test chose - including one that is deliberately wrong -
/// without carrying a fixture PDF for every case. Second, ingest's own ordering rules (extract only
/// what was actually registered; never re-extract a duplicate) are properties of the service, and a
/// property about how often something is called can only be asserted against a seam.
/// </para>
/// <para>
/// <b>This port is not part of the provider-agnosticism boundary.</b> <c>IDocumentContentProvider</c>
/// exists because document <em>presentation</em> differs sharply between providers; the text layer
/// does not. It is produced locally, offline, identically under every provider profile - the Claude
/// native-PDF profile, the generic vision profile and the text-only fallback all need exactly this -
/// and it is the one representation of the document that costs nothing per call.
/// </para>
/// </remarks>
public interface ITextLayerExtractor
{
    /// <summary>Extracts the per-page text of <paramref name="document"/>.</summary>
    /// <param name="document">
    /// The document to read, as the registry recorded it. Implementations open
    /// <see cref="DocumentReference.Location"/>'s local path and take
    /// <see cref="DocumentReference.DocumentId"/> as the identity of the resulting
    /// <see cref="TextLayer"/>.
    /// <para>
    /// <b>Implementations do not re-check containment and must not be given a reference that has not
    /// been through it.</b> A reference minted by <see cref="DocumentIngestService"/> is resolved,
    /// contained and round-trip stable by construction (see <see cref="DocumentReference.Location"/>);
    /// one built by hand carries only what its caller put in it, and opening that is opening whatever
    /// the caller named.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Token that cancels the extraction.</param>
    /// <returns>The text of every page of the document, keyed by logical page number.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="DocumentNotIngestibleException">
    /// The document is not one a text layer can be produced from. Implementations report this through
    /// the shared ingest refusal type rather than letting their parser's own exception hierarchy
    /// escape, so that a caller handles one refusal family and swapping the parser is not a breaking
    /// change to everyone who catches it.
    /// <see cref="DocumentNotIngestibleException.Reason"/> distinguishes the cases:
    /// <see cref="DocumentNotIngestibleReason.Unreadable"/> (damaged or not a PDF),
    /// <see cref="DocumentNotIngestibleReason.TooLarge"/> (past the configured size bound), and
    /// <see cref="DocumentNotIngestibleReason.UnsupportedPageNumbering"/> (the document renumbers its
    /// own pages, so page-keyed provenance could not be trusted).
    /// <para>
    /// <b>Two of the three reasons are deterministic in the document's bytes; one is not, and the
    /// difference matters.</b> <see cref="DocumentNotIngestibleReason.Unreadable"/> and
    /// <see cref="DocumentNotIngestibleReason.UnsupportedPageNumbering"/> are properties of the file
    /// itself - the same bytes are refused identically on any retry, on any host, under any
    /// configuration - which is what lets ingest treat a re-submission as a duplicate rather than as
    /// something worth retrying.
    /// </para>
    /// <para>
    /// <see cref="DocumentNotIngestibleReason.TooLarge"/> is neither. It compares a re-opened handle's
    /// length against a <em>configured</em> cap, so it depends on deployment configuration and on the
    /// state of the file at the moment of that second open - the same document is admitted under a
    /// larger cap, and a file swapped between the hash and the re-open is judged on bytes the registry
    /// never saw. It is a TOCTOU-sensitive, configuration-dependent verdict wearing the same exception
    /// type as the other two.
    /// </para>
    /// <para>
    /// <b>The consequence is worth stating plainly, because it is a real operational trap.</b> A
    /// document registers, is then refused <see cref="DocumentNotIngestibleReason.TooLarge"/>, and is
    /// thereafter permanently without a text layer: dedupe short-circuits every re-submission, so
    /// raising the cap or restoring the original file does not by itself cause a retry. Recovering
    /// that document is a run-level operation - re-driving preparation for an already-registered
    /// document - and it belongs to Sprint 03's <c>extraction_runs</c> and review queue, not to a
    /// second pass through the ingest gate.
    /// </para>
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no file at the document's location.</exception>
    /// <exception cref="IOException">
    /// The document could not be read: the share dropped, the handle died, the file system failed.
    /// <b>Implementations must let this propagate and must not rewrite it as a refusal.</b> An
    /// infrastructure fault is not a statement about the document, and reporting it as one tells an
    /// operator that a supplier sent a bad file when the network failed - and, because a refusal is
    /// terminal for the run, would leave a registered document permanently without a text layer over
    /// what was a transient outage.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// The parse exhausted memory. <b>Implementations must let this propagate too.</b> A PDF's
    /// streams are compressed and decompress into unbounded buffers, so this is the observable
    /// symptom of a decompression bomb - a resource-exhaustion attack indicator, not a data-quality
    /// outcome, and filing it as the latter would hide it from whoever watches for attacks.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<TextLayer> ExtractAsync(DocumentReference document, CancellationToken cancellationToken = default);
}
