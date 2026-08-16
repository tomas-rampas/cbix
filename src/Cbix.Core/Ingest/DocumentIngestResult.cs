using Cbix.Core.Documents;

namespace Cbix.Core.Ingest;

/// <summary>
/// What the ingest gate decided about one submission.
/// </summary>
/// <remarks>
/// The workflow reads <see cref="IsNewRegistration"/> to decide whether there is any work left to
/// do: a duplicate stops here, before triage and before a single model call is paid for
/// (design 5.1). Everything a continuing run needs downstream - identity, location, media type -
/// hangs off <see cref="Submitted"/>.
/// </remarks>
public sealed record DocumentIngestResult
{
    /// <summary>Initialises a new <see cref="DocumentIngestResult"/>.</summary>
    /// <param name="submitted">The document as read on this submission.</param>
    /// <param name="registered">
    /// The registry row now standing for these bytes: this submission's own on a first
    /// registration, the earlier one on a duplicate.
    /// </param>
    /// <param name="isNewRegistration"><see langword="true"/> when this submission created <paramref name="registered"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="submitted"/> or <paramref name="registered"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="submitted"/> and <paramref name="registered"/> do not share an identity.</exception>
    public DocumentIngestResult(DocumentReference submitted, DocumentRegistryEntry registered, bool isNewRegistration)
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(registered);

        if (!string.Equals(submitted.DocumentId, registered.DocumentId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The submitted document '{submitted.DocumentId}' and the registered entry '{registered.DocumentId}' are different documents.",
                nameof(registered));
        }

        Submitted = submitted;
        Registered = registered;
        IsNewRegistration = isNewRegistration;
    }

    /// <summary>Gets the reference to the document as read on this submission.</summary>
    public DocumentReference Submitted { get; }

    /// <summary>Gets the registry row standing for these bytes.</summary>
    public DocumentRegistryEntry Registered { get; }

    /// <summary>
    /// Gets a value indicating whether this submission created the registry row. When
    /// <see langword="false"/> the submission was a duplicate and the run does not continue.
    /// </summary>
    public bool IsNewRegistration { get; }

    /// <summary>Gets the content hash of the submitted bytes.</summary>
    public ContentHash ContentHash => Registered.ContentHash;
}
