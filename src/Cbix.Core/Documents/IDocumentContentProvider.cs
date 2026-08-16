namespace Cbix.Core.Documents;

/// <summary>
/// Presents a source document to a model in whatever form the active provider supports.
/// </summary>
/// <remarks>
/// <para>
/// Document presentation is where LLM providers differ most sharply, so it is the one thing
/// isolated behind a port (design 5.1). Three profiles implement it: the <b>Claude native-PDF</b>
/// profile (upload once to the Files API, reference the resulting file by id, set
/// <c>cache_control</c> so every later agent hits the prompt cache); the <b>generic-vision</b>
/// profile (the locally extracted text layer plus locally rendered page images, sent as ordinary
/// multimodal content); and the <b>text-only</b> profile (text layer alone, no visual matrix
/// reading, reported as degraded).
/// </para>
/// <para>
/// <b>Profile selection is not a caller's decision.</b> The active provider's capability profile
/// picks the implementation during composition; the ingest executor and the section agents call
/// this port and read <see cref="DocumentContent.Capabilities"/>. No caller may branch on which
/// profile is in play - if code outside a profile needs to know that, the port has failed.
/// </para>
/// <para>
/// <b>Nothing provider-specific may appear in this interface's signatures.</b> Provider payloads
/// travel inside <c>AIContent.RawRepresentation</c> within a profile implementation. This is
/// asserted structurally by the BDD scenario <c>DocumentContentProviderPort.feature</c>, which
/// walks the signatures and the property graph of every type they mention against an allowlist of
/// permitted assemblies, so a future provider package fails the scenario by default.
/// </para>
/// <para>
/// <b>Required lifetime: one provider instance per workflow-run scope.</b> The memo described on
/// <see cref="PrepareAsync"/> lives in the instance, which makes the DI registration part of the
/// contract rather than a detail: a singleton would cache uploads for the lifetime of the process
/// with no bound and would leak one document's prepared content into another run, while a
/// transient would re-upload on every agent call and defeat the point. The eviction bound is the
/// run scope itself - the memo dies with the run, and there is deliberately no process-lifetime
/// cache to size, expire or invalidate. Each of S01-05, S01-06 and S01-07 has to get this right
/// independently, which is why it is stated here rather than in one implementation.
/// </para>
/// </remarks>
public interface IDocumentContentProvider
{
    /// <summary>
    /// Prepares <paramref name="document"/> for presentation to a model, optionally redeeming a
    /// handle issued by an earlier run of the same document.
    /// </summary>
    /// <param name="document">The document to present, identified by its registry identity and location.</param>
    /// <param name="resumeFrom">
    /// A handle previously returned in <see cref="DocumentContent.Handle"/> and read back from run
    /// state or a checkpoint, or <see langword="null"/> for a first preparation. When supplied, the
    /// implementation must rebuild equivalent content from
    /// <see cref="DocumentContentHandle.ProviderToken"/> - reusing the uploaded file rather than
    /// uploading again. Rehydration is a parameter rather than a convention precisely because a
    /// convention is what gets forgotten in the third implementation.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the preparation.</param>
    /// <returns>
    /// The content blocks to attach to an agent's chat call, the capability descriptor stating
    /// whether the document was presented visually and whether the presentation is degraded, and
    /// the serializable handle to persist in run state.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="DocumentPreparationException">
    /// The document could not be presented. <see cref="DocumentPreparationException.IsTransient"/>
    /// tells the caller which response the failure wants: retry with backoff, or route to review.
    /// Implementations wrap provider and I/O failures in this exception rather than letting
    /// provider-specific exception types escape the profile - an exception type in the caller's
    /// catch block is as much a provider leak as one in a signature.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// <para>
    /// <b>One upload per document.</b> Any expensive preparation - an upload to a provider's files
    /// API, a page-image render - happens at most once per document for the lifetime of the
    /// provider instance. Ingest calls this once when a document enters the pipeline (S01-12), and
    /// every section agent then reuses the result.
    /// </para>
    /// <para>
    /// <b>The memo key is <see cref="DocumentReference.DocumentId"/> alone</b> - not the whole
    /// reference, and not any tuple involving it. The identity is a content hash, so two references
    /// that share it are the same bytes; keying on anything else (the location, the display name)
    /// would let a re-registered path or a renamed file pay for a second upload of a document the
    /// provider has already prepared.
    /// </para>
    /// <para>
    /// Implementations must therefore be safe to call repeatedly and concurrently for the same
    /// document, returning equivalent content each time, and must throw rather than return empty or
    /// partial content: silently returning less than requested would let a degraded run pass as a
    /// normal one.
    /// </para>
    /// </remarks>
    Task<DocumentContent> PrepareAsync(
        DocumentReference document,
        DocumentContentHandle? resumeFrom = null,
        CancellationToken cancellationToken = default);
}
