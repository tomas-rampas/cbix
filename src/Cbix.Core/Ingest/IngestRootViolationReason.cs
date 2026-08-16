namespace Cbix.Core.Ingest;

/// <summary>
/// Why the ingest gate refused a submission on containment grounds.
/// </summary>
/// <remarks>
/// Each value is a distinct escape route with a distinct detection point, so they are distinct
/// values: an operator reading "outside the ingest root" for a hard-linked file would go looking for
/// a path problem that does not exist.
/// </remarks>
public enum IngestRootViolationReason
{
    /// <summary>
    /// The submitted path, once normalised and fully link-resolved, does not lie under the
    /// configured ingest root.
    /// </summary>
    OutsideIngestRoot = 0,

    /// <summary>
    /// The submitted path is a UNC share or a device-namespace path (<c>\\?\</c>, <c>\\.\</c>).
    /// Refused on the string alone, before any file-system call: resolving such a path is itself the
    /// harm, because Windows authenticates outbound to the host it names.
    /// </summary>
    UncOrDevicePath = 1,

    /// <summary>
    /// The opened file has more than one hard link, so at least one other name reaches these same
    /// bytes - and that name may sit outside the ingest root. No amount of path resolution can see
    /// this, because a hard link is not a link the file system will resolve: it is a second, equal
    /// name for one file.
    /// </summary>
    MultiplyLinkedFile = 2,

    /// <summary>
    /// The resolved path cannot be carried in a <see cref="Uri"/> without changing meaning, so the
    /// location recorded for the document would not name the file whose bytes were read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="Uri"/> constructor percent-decodes and applies RFC 3986 dot-segment removal.
    /// Both are correct for a URI and wrong for a file name, because a percent sign is an ordinary
    /// character in a file name and a drop share full of URL-encoded names is routine. Measured:
    /// a real file <c>%2e%2e\evil.pdf</c> inside the root produced a location naming a file
    /// <em>outside</em> the root, and <c>annex%41.pdf</c> produced a location naming a
    /// non-existent <c>annexA.pdf</c>.
    /// </para>
    /// <para>
    /// This is a containment reason rather than a data-quality one because the artefact it prevents
    /// is a provenance record that points at the wrong file - the audit bar in design 8 asks which
    /// document a value came from, and a location that silently names a different file answers it
    /// falsely. The bytes may already have been read when this fires; what must never happen is that
    /// the record ships.
    /// </para>
    /// </remarks>
    UnrepresentableLocation = 3,
}
