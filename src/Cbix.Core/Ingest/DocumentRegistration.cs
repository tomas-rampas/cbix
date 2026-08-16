namespace Cbix.Core.Ingest;

/// <summary>
/// The outcome of offering a document to the registry: the row that is now stored, and whether this
/// submission is the one that created it.
/// </summary>
/// <remarks>
/// <see cref="Entry"/> is always the <em>stored</em> row, which on a duplicate is the one written by
/// the first submission - not the candidate just offered. The two can differ in everything except
/// identity: the same bytes may arrive later under a different file name or from a different path.
/// Callers that need what was just submitted must keep their own candidate; callers that need what
/// the pipeline considers authoritative read this.
/// </remarks>
public sealed record DocumentRegistration
{
    /// <summary>Initialises a new <see cref="DocumentRegistration"/>.</summary>
    /// <param name="entry">The registry row now stored under the candidate's identity.</param>
    /// <param name="isNewRegistration">
    /// <see langword="true"/> when this submission created <paramref name="entry"/>;
    /// <see langword="false"/> when an entry with the same content hash already existed, which is
    /// the duplicate-submission no-op of design 9.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    public DocumentRegistration(DocumentRegistryEntry entry, bool isNewRegistration)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        IsNewRegistration = isNewRegistration;
    }

    /// <summary>Gets the registry row stored under the submitted document's identity.</summary>
    public DocumentRegistryEntry Entry { get; }

    /// <summary>Gets a value indicating whether this submission is the one that created <see cref="Entry"/>.</summary>
    public bool IsNewRegistration { get; }
}
