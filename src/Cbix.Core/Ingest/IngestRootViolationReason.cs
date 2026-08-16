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
}
