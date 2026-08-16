namespace Cbix.Core.Ingest;

/// <summary>
/// The store of record for which document bytes have entered the pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The registry is what makes re-submission idempotent (design 8): a document is identified by the
/// hash of its bytes, so submitting it twice registers it once. It is also the pipeline's cheapest
/// gate - the duplicate is detected before any LLM call is paid for (design 5.1).
/// </para>
/// <para>
/// <b>Why there is no separate lookup method.</b> A <c>FindByHash</c> followed by an <c>Insert</c>
/// is a check-then-act race: two ingest workers handed the same document both find nothing, both
/// insert, and one of them either loses to a unique-key violation or - worse, on a schema without
/// one - succeeds, giving the same bytes two registry rows and two provenance trails. The single
/// atomic operation below has no such window, and it maps onto exactly what SQL Server can do
/// natively in Sprint 03 (an insert guarded by the primary key, with the duplicate-key violation
/// reported as <see cref="DocumentRegistration.IsNewRegistration"/> being <see langword="false"/>).
/// A read-only lookup can be added when a caller genuinely needs one; it must not become the
/// registration path.
/// </para>
/// <para>
/// <b>Implementations are the sole authority for document identity.</b> They persist the identity
/// carried by the candidate, which <see cref="DocumentRegistryEntry"/> has already tied to the hash
/// of the bytes read. Nothing in this port lets a caller name a document.
/// </para>
/// </remarks>
public interface IDocumentRegistry
{
    /// <summary>
    /// Registers <paramref name="candidate"/> if its content hash is not already registered, and
    /// otherwise returns the entry that is.
    /// </summary>
    /// <param name="candidate">The entry describing the bytes just read.</param>
    /// <param name="cancellationToken">Token that cancels the registration.</param>
    /// <returns>
    /// The stored entry - the candidate on a first submission, the pre-existing row on a duplicate -
    /// and which of the two happened.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// <para>
    /// <b>Atomic and idempotent.</b> Concurrent calls for the same content hash must produce exactly
    /// one stored entry, and exactly one of them may report
    /// <see cref="DocumentRegistration.IsNewRegistration"/> as <see langword="true"/>. Any number of
    /// repeat calls after that must report <see langword="false"/> and must not modify the stored
    /// entry: the registry row is written once, and the record of later submissions belongs in
    /// <see cref="IIngestAuditLog"/>.
    /// </para>
    /// <para>
    /// Implementations must throw rather than silently drop a registration. A registry that fails
    /// open would let the same document be extracted twice, at full model cost, and publish two
    /// provenance trails for one source.
    /// </para>
    /// </remarks>
    Task<DocumentRegistration> RegisterAsync(DocumentRegistryEntry candidate, CancellationToken cancellationToken = default);
}
