namespace Cbix.Core.Ingest;

/// <summary>
/// The configuration the ingest gate needs before it will read anything: where documents may come
/// from, and how large one is allowed to be.
/// </summary>
/// <remarks>
/// <para>
/// Both values are required and validated here, at the point of configuration, rather than
/// discovered per document. A root that can never contain anything, or an absent size bound, is a
/// deployment mistake; surfacing it when the service is constructed makes it a startup failure
/// instead of a failure that arrives once a document does - after the read, after the hash, with a
/// message about the document rather than about the configuration.
/// </para>
/// <para>
/// Shape only. Whether the root exists, and what it resolves to once symbolic links and junctions
/// are followed, is I/O and belongs to <see cref="DocumentIngestService"/>'s constructor.
/// </para>
/// </remarks>
public sealed record DocumentIngestOptions
{
    /// <summary>
    /// The Claude Files API's documented per-request PDF ceiling (32 MB, design 5.1), offered as the
    /// obvious reference point for <see cref="MaxDocumentBytes"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not a default. A size bound that a deployment never chose is a bound nobody owns,
    /// and this one has a real consequence: everything under it is read, hashed, and eventually paid
    /// for as provider input tokens.
    /// </remarks>
    public const long ClaudeFilesApiLimitBytes = 32L * 1024 * 1024;

    /// <summary>Initialises a new <see cref="DocumentIngestOptions"/>.</summary>
    /// <param name="ingestRoot">
    /// Fully qualified local path of the directory documents are read from. Every submission must
    /// resolve to a location under it.
    /// </param>
    /// <param name="maxDocumentBytes">
    /// Largest document, in bytes, that ingest will read. Enforced while reading, not merely
    /// announced: an unbounded read is an unbounded upload and an unbounded bill.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="ingestRoot"/> is empty or white space, is not fully qualified, or is a UNC or
    /// device-namespace path.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDocumentBytes"/> is not greater than zero.</exception>
    public DocumentIngestOptions(string ingestRoot, long maxDocumentBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ingestRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDocumentBytes);

        // Order matters: the UNC/device test is a pure string predicate and must run before anything
        // that could touch the file system, because resolving a UNC root is itself the harm.
        if (PathBoundary.HasUncOrDevicePrefix(ingestRoot))
        {
            throw new ArgumentException(
                "The ingest root must be a local path: a UNC share is a remote SMB fetch that leaks credentials to whichever host names it, "
                    + "and a device-namespace path bypasses the normalisation the containment check depends on.",
                nameof(ingestRoot));
        }

        if (!Path.IsPathFullyQualified(ingestRoot))
        {
            // A root resolved against the current directory would move with the process's working
            // directory, which is not a boundary anybody can reason about. Note that this test alone
            // is not enough - it returns true for \\server\share and for \\?\C:\x, which is why the
            // check above exists separately.
            throw new ArgumentException(
                $"The ingest root must be a fully qualified path; '{ingestRoot}' is not.",
                nameof(ingestRoot));
        }

        IngestRoot = ingestRoot;
        MaxDocumentBytes = maxDocumentBytes;
    }

    /// <summary>Gets the fully qualified local directory documents are read from, exactly as configured.</summary>
    public string IngestRoot { get; }

    /// <summary>Gets the largest document, in bytes, that ingest will read.</summary>
    public long MaxDocumentBytes { get; }
}
