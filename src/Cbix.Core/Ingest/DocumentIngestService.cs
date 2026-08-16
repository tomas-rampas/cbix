using System.Buffers;
using System.Security.Cryptography;

using Cbix.Core.Documents;

using Microsoft.Extensions.Logging;

namespace Cbix.Core.Ingest;

/// <summary>
/// The ingest gate: refuses anything reachable from outside the ingest root, hashes the bytes of an
/// incoming document, registers them once, and turns a re-submission into an audited no-op.
/// </summary>
/// <remarks>
/// <para>
/// This is code, never an agent (design 5.1): identity, deduplication and the decision to continue
/// are all deterministic, and they happen before any model call is paid for. The MAF executor that
/// wraps this service into the workflow graph arrives in S01-13; keeping the logic here rather than
/// in the executor is what lets it be tested without a workflow runtime, and reused by the backfill
/// path later.
/// </para>
/// <para>
/// <b>This service owns the containment half of the document trust boundary.</b>
/// <see cref="DocumentReference"/> enforces shape - absolute <c>file://</c>, no UNC, a bare display
/// name, a well-formed media type - and states that "the registry / ingest layer enforces
/// containment". Shape validation cannot see the configured root, and a containment check on an
/// unvalidated shape checks the wrong string; both halves are required. Containment runs in three
/// stages, in this order, because each one can only be trusted after the one before it:
/// </para>
/// <list type="number">
/// <item>
/// <b>String, no I/O.</b> A UNC or device-namespace path is refused on the raw string. This cannot
/// wait for resolution: merely resolving a UNC path makes Windows authenticate outbound to the host
/// the submitter named. The path is then normalised and prefix-checked against <em>either</em>
/// spelling of the root - configured or resolved - so an escape already visible in the string costs
/// zero file-system calls to refuse, while a legitimate path expressed under a linked root's own
/// name is not refused before anything has had the chance to discover that it is an alias. Stage 1
/// can only admit; stage 2 decides.
/// </item>
/// <item>
/// <b>Full resolution.</b> Every component from the root downward is followed to its final target -
/// each directory, then the leaf - and the prefix is re-checked. A junction or symlinked directory
/// in the middle of the path is a tunnel out of the root that leaf-only resolution never sees; on
/// Windows a junction needs no privilege to create. Resolving whole paths on both sides also removes
/// the asymmetry that made a symlinked ingest root refuse every absolute submission beneath it.
/// </item>
/// <item>
/// <b>Handle.</b> Once open, the file's hard-link count must be 1. A hard link is a second, equal
/// name for one file with nothing in the path to resolve, so it is invisible to stages 1 and 2 by
/// construction; the count on the opened handle is what sees it.
/// </item>
/// </list>
/// <para>
/// The bytes are read from the fully resolved path, and that is the path recorded in the registry.
/// Recording the submitted name while reading resolved bytes would make provenance state something
/// untrue about where a published value came from.
/// </para>
/// <para>
/// <b>Residual risks, stated rather than implied.</b> Junctions, symlinked directories, symlinked
/// leaves and hard links are defended above. What remains: (1) <b>TOCTOU</b> - a link swapped between
/// the checks and the open. The compensating control is deployment-level and recorded in the design
/// doc's Sprint 01 addendum: the ingest root must be write-restricted to the ingest service
/// principal, so no document supplier holds write access to the share, and the refusal events this
/// service logs are the instrument that monitors that assumption. (2) <b>Per-directory case
/// sensitivity</b> - the prefix comparison follows the operating system rather than probing each
/// directory; see <see cref="PathBoundary.PathComparison"/> for why that can only ever be stricter,
/// never looser. (3) <b>A stale root</b> - the ingest root is resolved once, at construction, and is
/// not re-validated afterwards, so a root link re-pointed while the service is running keeps being
/// judged against the directory it named at startup. Deliberate: re-resolving the root on every
/// submission would trade a real per-document cost for a case the write-restriction control already
/// covers, and a re-pointed root is a configuration change that warrants a restart. (4) <b>Paths
/// inside BCL exception messages</b> - a failure this service did not decide (a denied ACL, a path
/// shape the platform refuses) carries the resolved path in its own <c>Message</c>. Its structured
/// event here records only the exception type and the sanitised path, but the exception itself
/// propagates unaltered, so anything that logs it logs an unsanitised path. Bounded by the same
/// trusted-log-sink assumption, and the reason the exception type is not rewritten: a caller
/// diagnosing a share outage needs the real failure.
/// </para>
/// <para>
/// <b>A refused path is not written to the ingest audit log.</b> That log is document-scoped: every
/// entry carries a content hash and a document reference, and a path refused before a single byte
/// was read has neither. Inventing a null-shaped entry for it would weaken the type for every real
/// entry. Refusals are instead logged as structured error events here - with the paths as fields,
/// never interpolated into message text - so a SIEM sees them whether or not any caller catches the
/// exception.
/// </para>
/// </remarks>
public sealed partial class DocumentIngestService
{
    /// <summary>Read buffer for the bounded hashing pass. Matches the framework's default copy buffer.</summary>
    private const int ReadBufferSize = 81920;

    /// <summary>
    /// Placeholder identity for the pre-read construction probe. Shaped like a real content hash so
    /// it passes the identity checks, and never stored: the real identity is derived from the bytes.
    /// </summary>
    private const string ProbeDocumentId = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private readonly IDocumentRegistry _registry;
    private readonly IIngestAuditLog _auditLog;
    private readonly ITextLayerExtractor _textLayerExtractor;
    private readonly ILogger<DocumentIngestService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly DocumentIngestOptions _options;
    private readonly string _configuredRoot;
    private readonly string _ingestRoot;

    /// <summary>Initialises a new <see cref="DocumentIngestService"/>.</summary>
    /// <param name="registry">The store of record for documents that have entered the pipeline.</param>
    /// <param name="auditLog">The append-only trail of ingest decisions.</param>
    /// <param name="options">Where documents may be read from, and how large one may be.</param>
    /// <param name="textLayerExtractor">
    /// Produces the local text layer for each newly registered document - the grounding corpus of
    /// design 5.6.
    /// </param>
    /// <param name="logger">Sink for the structured refusal events that make containment observable.</param>
    /// <param name="timeProvider">
    /// Clock used for audit and registration timestamps. Defaults to
    /// <see cref="TimeProvider.System"/>; tests inject a controlled clock so that "first seen" and
    /// "recorded at" are assertable rather than approximately now.
    /// </param>
    /// <exception cref="ArgumentNullException">Any required argument is <see langword="null"/>.</exception>
    /// <exception cref="IOException">
    /// A cyclic link was found while resolving the configured ingest root, or the file system failed
    /// while reading it. A root that does not exist is <em>not</em> an error here: missing components
    /// are carried through unresolved by design, because an unmounted share is an operational state
    /// that resolves itself, and each submission reports the absence properly when it tries to read.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The process may not traverse the configured ingest root's path.</exception>
    public DocumentIngestService(
        IDocumentRegistry registry,
        IIngestAuditLog auditLog,
        DocumentIngestOptions options,
        ITextLayerExtractor textLayerExtractor,
        ILogger<DocumentIngestService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(textLayerExtractor);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _auditLog = auditLog;
        _textLayerExtractor = textLayerExtractor;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Two spellings of one directory, and both are needed.
        //
        // _ingestRoot is the fully resolved one, judged against in stage 2 and therefore the
        // authority. Resolving only this side is what made a linked root - the norm for mounted
        // shares, and for temporary directories on macOS - refuse every absolute path beneath it.
        //
        // _configuredRoot is what the operator wrote, normalised but not link-resolved. Stage 1 has
        // to accept it as well, because a caller who submits an absolute path under the root's
        // configured name is submitting a legitimate alias of the same directory, and stage 1 runs
        // before any resolution could discover that. Being lenient there costs nothing: stage 2
        // resolves and re-judges everything stage 1 admits, so the only effect is that such a path
        // reaches stage 2 instead of being refused a moment earlier.
        _configuredRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.IngestRoot));
        _ingestRoot = Path.TrimEndingDirectorySeparator(ResolveEveryComponent(_configuredRoot));
    }

    /// <summary>
    /// Reads the document at <paramref name="documentPath"/>, registers its content hash if these
    /// bytes are new, and records the outcome in the audit trail.
    /// </summary>
    /// <param name="documentPath">
    /// Path of the document, absolute or relative to the ingest root. It must resolve to a location
    /// under the ingest root.
    /// </param>
    /// <param name="mediaType">IANA media type of the document bytes. Defaults to <c>application/pdf</c>.</param>
    /// <param name="cancellationToken">Token that cancels the read and the registration.</param>
    /// <returns>
    /// The reference to the document as read, the registry entry now standing for these bytes,
    /// whether this submission was the one that created it, and - only when it did - the local text
    /// layer extracted for it.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="documentPath"/> or <paramref name="mediaType"/> is empty or white space, or
    /// the resolved document does not satisfy <see cref="DocumentReference"/>'s construction rules.
    /// </exception>
    /// <exception cref="IngestRootViolationException">
    /// The submission is reachable from outside the configured ingest root: it resolves outside it,
    /// it is a UNC or device path, or the opened file has more than one hard link. Nothing is read
    /// past the point of refusal and nothing is registered.
    /// </exception>
    /// <exception cref="DocumentNotIngestibleException">
    /// The submission is inside the root but is not a usable document: a directory, an empty file,
    /// one larger than the configured maximum, or - raised after registration, by the text-layer
    /// extraction step - a file the local PDF parser cannot read.
    /// </exception>
    /// <exception cref="FileNotFoundException">There is no file at the resolved path.</exception>
    /// <exception cref="DirectoryNotFoundException">Part of the resolved path does not exist.</exception>
    /// <exception cref="IOException">The document could not be read.</exception>
    /// <exception cref="PlatformNotSupportedException">The host cannot report a hard-link count, so containment cannot be proven.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// Idempotent by construction (design 8): the same bytes always produce the same identity, so
    /// re-submitting a document leaves the registry with one row, one <c>FirstSeenUtc</c>, and one
    /// provenance trail - and adds one audit entry per submission, because the repetition itself is
    /// worth recording (design 9, "Duplicate submission").
    /// </remarks>
    public async Task<DocumentIngestResult> IngestAsync(
        string documentPath,
        string mediaType = "application/pdf",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        string resolvedPath = ResolveWithinIngestRoot(documentPath);
        string leafName = Path.GetFileName(resolvedPath);

        // Both of these run before the file is opened: a document that cannot be represented, or
        // whose name can never construct a reference, is refused without paying for a full read and
        // hash of it.
        Uri location = BuildContainedLocation(documentPath, resolvedPath);
        AssertReferenceIsConstructible(location, leafName, mediaType);

        FileStreamOptions readOptions = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };

        ContentHash contentHash;
        long byteLength;

        try
        {
            await using FileStream content = new(resolvedPath, readOptions);

            RefuseMultiplyLinkedFile(documentPath, resolvedPath, content);

            (contentHash, byteLength) = await ReadAndHashAsync(resolvedPath, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not DocumentNotIngestibleException
            and not IngestRootViolationException
            and not OperationCanceledException)
        {
            // Failures raised by the BCL on the open or the read - a missing file, a denied ACL, a
            // path shape the platform refuses such as an alternate data stream - would otherwise
            // leave no trace in the security log at all, because they are not refusals this service
            // decided. Only the exception's type is recorded: its Message frequently embeds the
            // resolved path, and this is the one logging site that has not sanitised it.
            LogDocumentReadFailed(_logger, PathBoundary.ForLog(resolvedPath), error.GetType().FullName ?? error.GetType().Name);
            throw;
        }

        if (byteLength == 0)
        {
            throw RefuseDocument(DocumentNotIngestibleReason.Empty, resolvedPath, byteLength);
        }

        // Identity comes from the hash of the bytes just read - never from anything the submitter
        // supplied. This is the single place in CBIX where a DocumentId is minted. The location is
        // the one already proven to round-trip, not a second Uri built from the same string.
        DocumentReference submitted = new(contentHash.Canonical, location, leafName, mediaType);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DocumentRegistryEntry candidate = new(contentHash, submitted, byteLength, now);

        DocumentRegistration registration = await _registry.RegisterAsync(candidate, cancellationToken).ConfigureAwait(false);

        // Register first, then audit. Neither order is atomic until both share a SQL transaction in
        // Sprint 03, so the choice is which inconsistency to prefer on a crash between them. A
        // registry row whose audit entry is missing is the lesser harm: evidence that lies is worse
        // than evidence that is incomplete. Be precise about the cost, though - re-submitting does
        // NOT repair the gap, because the document is now registered and the second submission
        // records DuplicateSubmissionIgnored. The registration event is lost for good; that is the
        // accepted trade until one transaction covers both, and it is why the audit failure below is
        // propagated loudly rather than swallowed.
        //
        // The audit entry also refuses a first-registered instant later than the recorded one, which
        // a clock running backwards across two workers could produce for a duplicate. That is left as
        // a loud failure on the no-op path rather than papered over with a fabricated timestamp: once
        // Sprint 03 takes both instants from the database clock the case cannot arise at all.
        IngestAuditEntry auditEntry = new(
            registration.IsNewRegistration
                ? IngestAuditEventType.DocumentRegistered
                : IngestAuditEventType.DuplicateSubmissionIgnored,
            contentHash,
            submitted,
            now,
            registration.Entry.FirstSeenUtc);

        await _auditLog.RecordAsync(auditEntry, cancellationToken).ConfigureAwait(false);

        // Document preparation, and it runs last of the three for two separate reasons.
        //
        // After the registry, because the registry is what decides whether there is any work left:
        // a duplicate's run stops there (design 5.1), so preparing it would be work done for a run
        // that does not continue - free today, a Files API upload and provider tokens once S01-12
        // joins this step. `IsNewRegistration` is the short-circuit, and it can only be known after
        // RegisterAsync.
        //
        // After the audit entry, because the registration genuinely happened and the trail has to
        // say so whatever follows. Slotting a step that fails on ordinary bad input between the
        // registry write and its audit entry would manufacture, on a routine failure path, exactly
        // the registered-but-unaudited gap the comment above tolerates only for a crash.
        //
        // The cost of that ordering, stated rather than implied: a document whose bytes register
        // fine but which is then refused by preparation ends up registered, with a DocumentRegistered
        // entry, and this call throws. Re-submitting does not repair it - the document is now known,
        // so the second submission is a duplicate and never re-extracts.
        //
        // That is sound for the refusals that are deterministic in the bytes. Unreadable and
        // UnsupportedPageNumbering describe the file itself, so the same file is refused identically
        // on any retry: there is nothing to gain by re-running preparation for it, and the registry is
        // behaving as designed - it records document identity, not run state.
        //
        // It is NOT sound for TooLarge, and the ordering does not pretend otherwise. That refusal
        // tests a re-opened handle against a configured cap, so it is configuration-dependent and
        // TOCTOU-sensitive: raising the cap, or restoring a file that was swapped between the hash and
        // the re-open, would make the same document extractable - but dedupe short-circuits the
        // re-submission, so it stays text-less until something re-drives preparation for an
        // already-registered document. That recovery path is Sprint 03's extraction_runs and review
        // queue; see ITextLayerExtractor, which records the same split on the contract itself.
        //
        // Transient failures are deliberately kept out of that set. An IOException from a share that
        // dropped, and an OutOfMemoryException from a decompression bomb, propagate as themselves
        // rather than being rewritten as refusals; the run fails and the document keeps whatever
        // registry state it had, instead of a passing network fault permanently marking a document as
        // one that has no text layer. Recovering a run whose preparation failed belongs to the
        // extraction_runs table and the review queue in Sprint 03, not to a second pass through the
        // ingest gate.
        TextLayer? textLayer = registration.IsNewRegistration
            ? await _textLayerExtractor.ExtractAsync(submitted, cancellationToken).ConfigureAwait(false)
            : null;

        return new DocumentIngestResult(submitted, registration.Entry, registration.IsNewRegistration, textLayer);
    }

    /// <summary>
    /// Runs containment stages 1 and 2 and returns the fully resolved path, or refuses.
    /// </summary>
    private string ResolveWithinIngestRoot(string documentPath)
    {
        // Stage 1, on the string alone. This must precede every file-system call: resolving a UNC
        // path is itself the harm, not a step towards discovering harm.
        if (PathBoundary.HasUncOrDevicePrefix(documentPath))
        {
            throw RefuseContainment(IngestRootViolationReason.UncOrDevicePath, documentPath, documentPath);
        }

        // GetFullPath with a base directory normalises "..", "." and separators, so traversal is
        // collapsed before it is judged rather than pattern-matched for in the raw string. It is pure
        // string manipulation and performs no I/O.
        // Relative paths resolve against the root's configured spelling - the one the operator wrote
        // and the one an operational runbook will quote.
        string normalised = Path.GetFullPath(documentPath, _configuredRoot);

        if (PathBoundary.HasUncOrDevicePrefix(normalised))
        {
            throw RefuseContainment(IngestRootViolationReason.UncOrDevicePath, documentPath, normalised);
        }

        if (string.Equals(normalised, _ingestRoot, PathBoundary.PathComparison)
            || string.Equals(normalised, _configuredRoot, PathBoundary.PathComparison))
        {
            // The root itself is not an escape attempt, it is a caller that meant to enumerate.
            throw RefuseDocument(DocumentNotIngestibleReason.NotAFile, normalised);
        }

        // Under either spelling of the root is enough to proceed; stage 2 is what decides.
        if (!PathBoundary.IsUnder(_ingestRoot, normalised) && !PathBoundary.IsUnder(_configuredRoot, normalised))
        {
            throw RefuseContainment(IngestRootViolationReason.OutsideIngestRoot, documentPath, normalised);
        }

        // Stage 2, with I/O: follow every component to its final target and judge that.
        string resolved = ResolveEveryComponent(normalised);

        if (PathBoundary.HasUncOrDevicePrefix(resolved))
        {
            // A link may point at a UNC location; the string check has to be repeated on the result.
            throw RefuseContainment(IngestRootViolationReason.UncOrDevicePath, documentPath, resolved);
        }

        if (!PathBoundary.IsUnder(_ingestRoot, resolved))
        {
            throw RefuseContainment(IngestRootViolationReason.OutsideIngestRoot, documentPath, resolved);
        }

        if (Directory.Exists(resolved))
        {
            throw RefuseDocument(DocumentNotIngestibleReason.NotAFile, resolved);
        }

        return resolved;
    }

    /// <summary>
    /// Builds the document's location and proves the <see cref="Uri"/> did not change which file it
    /// names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <see cref="Uri"/> is not a container for a file path.</b> Its constructor percent-decodes
    /// and applies RFC 3986 dot-segment removal - both correct for a URI, both wrong for a file name,
    /// because <c>%</c> is an ordinary character in a file name. Measured on a real tree: a file
    /// <c>%2e%2e\evil.pdf</c> inside the root was hashed correctly while its location named a file
    /// <em>outside</em> the root, and <c>annex%41.pdf</c> produced a location naming a non-existent
    /// <c>annexA.pdf</c>. In both cases the digest described one file and the provenance described
    /// another.
    /// </para>
    /// <para>
    /// <b>The class of names this actually refuses is narrow, and worth stating precisely so nobody
    /// over-reads the paragraph above.</b> Measured on .NET 10: only a literal <c>%</c> followed by
    /// two hexadecimal digits breaks the round trip. A bare <c>%</c>, <c>#</c>, <c>?</c>, spaces,
    /// umlauts, CJK characters and emoji all survive it unchanged. So this refusal costs a real
    /// deployment approximately nothing - a document named with a literal <c>%2e</c> sequence is a
    /// genuine rarity, not a routine drop-share name.
    /// </para>
    /// <para>
    /// <b>That makes the round-trip assertion a permanent design decision, not a stopgap awaiting a
    /// proper escaping scheme.</b> Hand-rolling an escaper for every quirk of the URI grammar would
    /// buy the ability to accept a vanishingly rare name in exchange for a second, subtler place
    /// where a path can be silently rewritten. Asserting instead is the difference between believing
    /// a conversion is lossless and checking that this one was: the location must render back to
    /// exactly the path that was resolved, and must still lie under the ingest root. Anything else is
    /// refused, and the refusal is a named, logged outcome rather than a corrupted provenance record.
    /// </para>
    /// </remarks>
    private Uri BuildContainedLocation(string documentPath, string resolvedPath)
    {
        Uri location = new(resolvedPath);

        bool roundTrips = string.Equals(location.LocalPath, resolvedPath, PathBoundary.PathComparison)
            && PathBoundary.IsUnder(_ingestRoot, location.LocalPath);

        if (!roundTrips)
        {
            throw RefuseContainment(IngestRootViolationReason.UnrepresentableLocation, documentPath, resolvedPath);
        }

        return location;
    }

    /// <summary>
    /// Runs <see cref="DocumentReference"/>'s own construction rules against the resolved document
    /// before anything is read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules are not restated here - the constructor is called with a placeholder identity and
    /// the result discarded - because a second copy of "what makes a file name acceptable" would
    /// drift from the first, and the copy that drifts is always the one guarding the cheap path. The
    /// real reference is built from the digest once the bytes are read, and its constructor remains
    /// the backstop; this only moves the failure earlier, so a name that can never be accepted does
    /// not first pay for a full read and hash.
    /// </para>
    /// <para>
    /// <b>The probe is wrapped so that this refusal is logged like every other one.</b> It was
    /// previously the single refusal class in this service that emitted no structured event - the
    /// constructor's <see cref="ArgumentException"/> simply flew - which contradicted the class
    /// doc's promise that a refusal is always visible to a SIEM whether or not a caller catches it.
    /// The gap mattered more than most: the inputs this rejects are the deliberately hostile ones -
    /// a U+202E display name that renders <c>annex&lt;RLO&gt;fdp.exe</c> as <c>annexexe.pdf</c>, a
    /// control character aimed at a log line, a reserved Windows device name - so it was precisely
    /// the attacks nobody was told about.
    /// </para>
    /// <para>
    /// The exception is rethrown unchanged rather than translated. Its message names the rule that
    /// was broken, which is what a supplier fixing their file name needs, and re-wrapping it would
    /// change an established refusal contract for the sake of logging. Only the file name is
    /// recorded - sanitised, because a name being refused for containing a format character is a
    /// name that must not be rendered raw into the very log the refusal is reported in. The
    /// propagating exception still carries the raw name in its own <c>Message</c>, so anything that
    /// logs the exception logs it unsanitised; that is the same bounded residual as item (4) on the
    /// class doc, and the structured event here is the one an alerting pipeline should key on.
    /// </para>
    /// </remarks>
    private void AssertReferenceIsConstructible(Uri location, string fileName, string mediaType)
    {
        try
        {
            _ = new DocumentReference(ProbeDocumentId, location, fileName, mediaType);
        }
        catch (ArgumentException error)
        {
            LogUnacceptableReferenceShape(
                _logger,
                PathBoundary.ForLog(fileName),
                error.ParamName ?? "unknown",
                error.GetType().FullName ?? error.GetType().Name);

            throw;
        }
    }

    /// <summary>Containment stage 3: the opened file must have exactly one name.</summary>
    private void RefuseMultiplyLinkedFile(string documentPath, string resolvedPath, FileStream content)
    {
        uint linkCount = FileLinkCount.Read(content.SafeFileHandle);

        if (linkCount > 1)
        {
            throw RefuseContainment(IngestRootViolationReason.MultiplyLinkedFile, documentPath, resolvedPath);
        }
    }

    /// <summary>
    /// Reads the stream once, hashing as it goes and counting what it actually hashed, abandoning the
    /// read the moment the configured maximum is passed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The length comes from the read, not from the file's metadata.</b> A stream's reported
    /// <see cref="Stream.Length"/> is a claim made before the read and can differ from what the read
    /// returns - a file appended to concurrently, a filesystem that reports optimistically. Recording
    /// the claim next to a hash of the reality would put two mutually inconsistent facts in one
    /// registry row, and <c>byte_length</c> is the column an operator uses to sanity-check the digest.
    /// </para>
    /// <para>
    /// <b>The cap is enforced during the read.</b> Checking <see cref="Stream.Length"/> up front and
    /// then reading unboundedly trusts the same claim; the counter below cannot be lied to. Nothing
    /// oversized is ever fully read, which matters because everything read here is eventually paid for
    /// as provider input tokens.
    /// </para>
    /// </remarks>
    private async Task<(ContentHash Hash, long ByteLength)> ReadAndHashAsync(
        string resolvedPath,
        Stream content,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        long byteLength = 0;

        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, ReadBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                byteLength += read;

                if (byteLength > _options.MaxDocumentBytes)
                {
                    throw RefuseDocument(DocumentNotIngestibleReason.TooLarge, resolvedPath, byteLength);
                }

                hasher.AppendData(buffer, 0, read);
            }

            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            hasher.GetCurrentHash(digest);

            return (ContentHash.FromSha256Digest(digest), byteLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Follows every component of an absolute path to its final link target, from the root downward.
    /// </summary>
    /// <remarks>
    /// Resolving only the leaf is the gap a junction walks through: <c>root/link/document.pdf</c> has
    /// an entirely ordinary leaf, and it is the directory above it that leaves the root. Each segment
    /// is therefore rebuilt onto the already-resolved prefix, so a link found halfway down redirects
    /// everything after it, exactly as the file system will when the file is opened.
    /// </remarks>
    private static string ResolveEveryComponent(string absolutePath)
    {
        string pathRoot = Path.GetPathRoot(absolutePath) ?? string.Empty;

        if (pathRoot.Length == 0)
        {
            return absolutePath;
        }

        string current = pathRoot;

        foreach (string segment in absolutePath[pathRoot.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            // Path.Join, not Path.Combine: Combine throws away everything accumulated so far the
            // moment a segment looks rooted, so a segment such as "c:evil.pdf" would silently
            // restart the path rather than extend it. That happens to fail closed here, but only
            // incidentally - Join simply concatenates, which is what this loop actually means.
            current = Path.Join(current, segment);

            // Directory first: a junction and a symlinked directory are both directories, and asking
            // FileInfo about one reads oddly even though it resolves.
            //
            // A component that does not exist is carried through unresolved rather than queried:
            // ResolveLinkTarget throws FileNotFoundException on a path that is not there, and a root
            // that is not mounted yet is an operational state, not a configuration error. Nothing is
            // lost by skipping it - a name that resolves to nothing has no bytes to escape with, and
            // the open call below reports the absence properly. A dangling link is the same case: it
            // reads as non-existent, stays inside the root, and fails to open.
            FileSystemInfo? entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current) : null;

            if (entry?.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
            }
        }

        return current;
    }

    /// <summary>
    /// Logs the refusal as a structured security event and returns the exception for the caller to
    /// throw.
    /// </summary>
    /// <remarks>
    /// Returning rather than throwing keeps <c>throw Refuse(...)</c> at the call site, so the control
    /// flow is visible to a reader and to the compiler's definite-assignment analysis. Every
    /// containment refusal goes through here, which is what makes "a refusal is always logged" a
    /// property of the code rather than a convention six call sites have to remember.
    /// </remarks>
    private IngestRootViolationException RefuseContainment(
        IngestRootViolationReason reason,
        string submittedPath,
        string resolvedPath)
    {
        LogContainmentRefusal(
            _logger,
            reason,
            PathBoundary.ForLog(submittedPath),
            PathBoundary.ForLog(resolvedPath),
            PathBoundary.ForLog(_ingestRoot));

        return new IngestRootViolationException(reason, submittedPath, resolvedPath, _ingestRoot);
    }

    /// <summary>
    /// Logs a not-a-document refusal as a structured event and returns the exception for the caller
    /// to throw.
    /// </summary>
    /// <remarks>
    /// The sanitisation happens here and in <see cref="RefuseContainment"/> only, so there is exactly
    /// one place per refusal family where a raw path could reach a log - and neither of them does.
    /// The exception keeps the unsanitised path on its typed property, which is for code to read, not
    /// for a log to render.
    /// </remarks>
    private DocumentNotIngestibleException RefuseDocument(
        DocumentNotIngestibleReason reason,
        string documentPath,
        long? byteLength = null)
    {
        LogDocumentRefused(_logger, reason, PathBoundary.ForLog(documentPath), byteLength);

        return new DocumentNotIngestibleException(reason, documentPath, byteLength);
    }

    /// <summary>
    /// Structured containment-refusal event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every path reaching this method has already been through <see cref="PathBoundary.ForLog"/>,
    /// which is why the template may name them: as named placeholders they are captured as
    /// structured fields by any sink worth the name, and the sanitisation is what makes rendering
    /// them into a line safe. The alternative - fields with no placeholder - the source generator
    /// rejects outright (SYSLIB1015), and hand-rolling a state object to defeat that check would
    /// hide the values from exactly the sinks that need them.
    /// </para>
    /// <para>
    /// This is the security signal for the deployment control recorded in the design doc's Sprint 01
    /// addendum: the ingest root must be write-restricted to the ingest service principal, and these
    /// events are how a breach of that assumption becomes visible.
    /// </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 1010,
        Level = LogLevel.Error,
        Message = "Ingest refused a submission at the containment boundary: {Reason}. Submitted='{SubmittedPath}', resolved='{ResolvedPath}', root='{IngestRoot}'.")]
    private static partial void LogContainmentRefusal(
        ILogger logger,
        IngestRootViolationReason reason,
        string submittedPath,
        string resolvedPath,
        string ingestRoot);

    /// <summary>Structured event for a submission inside the root that is not a usable document.</summary>
    /// <remarks>See <see cref="LogContainmentRefusal"/> on why the sanitised paths are named in the template.</remarks>
    [LoggerMessage(
        EventId = 1011,
        Level = LogLevel.Error,
        Message = "Ingest refused a submission as not a usable document: {Reason}. Document='{DocumentPath}', bytes read={ByteLength}.")]
    private static partial void LogDocumentRefused(
        ILogger logger,
        DocumentNotIngestibleReason reason,
        string documentPath,
        long? byteLength);

    /// <summary>
    /// Structured event for an open or read that failed for a reason this service did not decide.
    /// </summary>
    /// <remarks>
    /// Only the exception's type name is recorded. BCL exception messages routinely embed the path
    /// they failed on, and this is the one path into the log that has not been through
    /// <see cref="PathBoundary.ForLog"/>; the sanitised path is supplied separately.
    /// </remarks>
    [LoggerMessage(
        EventId = 1012,
        Level = LogLevel.Error,
        Message = "Ingest could not read a submitted document. Document='{DocumentPath}', failure={FailureType}.")]
    private static partial void LogDocumentReadFailed(ILogger logger, string documentPath, string failureType);

    /// <summary>
    /// Structured event for a submission whose leaf name or media type can never construct a
    /// <see cref="DocumentReference"/>.
    /// </summary>
    /// <remarks>
    /// Its own event id rather than a reuse of <see cref="LogDocumentRefused"/>'s: this refusal is
    /// decided by <see cref="DocumentReference"/>'s shape rules and carries a parameter name instead
    /// of a <see cref="DocumentNotIngestibleReason"/>, and folding two different refusal shapes onto
    /// one id would make an alert rule unable to tell them apart. The file name has been through
    /// <see cref="PathBoundary.ForLog"/>, which is what makes naming it in the template safe - a
    /// name refused for carrying U+202E is exactly the string that must not be rendered raw here.
    /// </remarks>
    [LoggerMessage(
        EventId = 1014,
        Level = LogLevel.Error,
        Message = "Ingest refused a submission whose reference shape is unacceptable. FileName='{FileName}', parameter={ParameterName}, failure={FailureType}.")]
    private static partial void LogUnacceptableReferenceShape(
        ILogger logger,
        string fileName,
        string parameterName,
        string failureType);
}
