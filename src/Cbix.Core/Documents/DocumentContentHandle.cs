namespace Cbix.Core.Documents;

/// <summary>
/// The serializable half of a prepared document: everything needed to get the same document
/// content back from a provider after a workflow run resumes from a checkpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> <see cref="DocumentContent"/> is an in-process value: its content
/// blocks carry provider payloads in <c>AIContent.RawRepresentation</c>, which is
/// <c>[JsonIgnore]</c> and therefore silently lost through a System.Text.Json round trip - the
/// serializer MAF superstep checkpointing uses. Persisting <see cref="DocumentContent"/> would
/// produce a checkpoint that deserialises without error and without the document. This handle is
/// deliberately BCL-only and round-trippable, and it is the thing run state and checkpoints
/// persist.
/// </para>
/// <para>
/// <b>How it is used.</b> Ingest (S01-12) stores the handle in run state. After a crash or a
/// human-review pause, the workflow hands the handle back to
/// <see cref="IDocumentContentProvider.PrepareAsync"/>, which rebuilds equivalent content from
/// <see cref="ProviderToken"/> instead of repeating the upload or render. That is what keeps the
/// "a resumed run repeats no LLM or upload work" guarantee true across a process restart.
/// </para>
/// </remarks>
public sealed record DocumentContentHandle
{
    /// <summary>Initialises a new <see cref="DocumentContentHandle"/>.</summary>
    /// <param name="documentId">Registry identity of the document, matching <see cref="DocumentReference.DocumentId"/>.</param>
    /// <param name="profileName">
    /// Name of the profile that issued the handle. A handle is only meaningful to the profile that
    /// created it: <see cref="ProviderToken"/> means whatever that profile decided it means, so a
    /// resume must route the handle back to the same profile.
    /// </param>
    /// <param name="providerToken">
    /// Opaque, profile-defined resume token - the Claude profile stores its Files API
    /// <c>file_id</c> here; a profile with nothing to resume from stores <see langword="null"/> and
    /// rebuilds its content from local inputs. Treat it as opaque: no caller outside the issuing
    /// profile may parse it.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="documentId"/> or <paramref name="profileName"/> is empty or white space.</exception>
    public DocumentContentHandle(string documentId, string profileName, string? providerToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        DocumentId = documentId;
        ProfileName = profileName;
        ProviderToken = providerToken;
    }

    /// <summary>Gets the registry identity of the document this handle refers to.</summary>
    public string DocumentId { get; }

    /// <summary>Gets the name of the profile that issued this handle and that must be used to redeem it.</summary>
    public string ProfileName { get; }

    /// <summary>Gets the opaque, profile-defined resume token, or <see langword="null"/> when the profile needs none.</summary>
    public string? ProviderToken { get; }

    /// <summary>
    /// Reports whether this handle can be redeemed by the named profile for the given document,
    /// having first rejected the one case that is never redeemable and never recoverable: a handle
    /// belonging to a different document.
    /// </summary>
    /// <param name="document">The document the caller is preparing.</param>
    /// <param name="profileName">The name of the profile attempting the redemption.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="profileName"/> is the profile that issued this
    /// handle, so <see cref="ProviderToken"/> is meaningful to it; <see langword="false"/> when the
    /// handle belongs to a different profile, in which case the caller re-prepares from scratch and
    /// issues a new handle. A profile mismatch is an expected operational state after a provider
    /// swap, not an error.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="profileName"/> is empty or white space, or - the case this guard exists for -
    /// <paramref name="document"/> is not the document this handle was issued for.
    /// </exception>
    /// <remarks>
    /// The asymmetry between the two mismatches is deliberate. A wrong <b>profile</b> costs one
    /// repeated upload and is recovered by re-preparing. A wrong <b>document</b> cannot be recovered
    /// by anything, because both of its silent outcomes are corruption: the caller gets content
    /// prepared from another document (cache poisoning through a mismatched memo entry), and every
    /// value extracted from it is recorded against the wrong source (mis-attributed provenance,
    /// which the audit bar in design 8 exists to prevent). Returning <see langword="false"/> there
    /// would let a caller "handle" it by carrying on with the wrong document, so it throws.
    /// </remarks>
    public bool IsRedeemableBy(DocumentReference document, string profileName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        if (!string.Equals(DocumentId, document.DocumentId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"This handle was issued for document '{DocumentId}' but was offered for document "
                    + $"'{document.DocumentId}'. Redeeming it would prepare one document's content under "
                    + "another document's identity.",
                nameof(document));
        }

        return string.Equals(ProfileName, profileName, StringComparison.Ordinal);
    }
}
