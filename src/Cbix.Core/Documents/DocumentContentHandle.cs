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
}
