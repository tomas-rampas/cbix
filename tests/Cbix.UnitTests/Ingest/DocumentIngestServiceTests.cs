using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Covers the ingest gate end to end against a real temporary file tree: identity derived from the
/// bytes read, one registry row per distinct document, an audit entry per submission, and - the
/// larger part of this file - the containment boundary that <c>DocumentReference</c> delegates here.
/// <para>
/// The containment tests are probes, not assertions about intent: each one builds the actual escape
/// (a junction, a directory symlink, a hard link, a traversal, a UNC path) against a real file
/// outside the root and requires ingest to refuse it. A probe whose precondition the host denies is
/// skipped visibly with <c>Skip.If</c> rather than returning early, because a security probe that
/// quietly reports success is worse than one that is missing.
/// </para>
/// </summary>
public sealed class DocumentIngestServiceTests : IDisposable
{
    private static readonly DateTimeOffset FirstSubmission = new(2026, 8, 16, 9, 30, 0, TimeSpan.Zero);

    private const long MaxDocumentBytes = 4096;

    /// <summary>
    /// Event ids this service emits, pinned so a renumbering breaks a test rather than an alert rule.
    /// </summary>
    /// <remarks>
    /// The id is the part of a log event a SIEM actually keys on: message text is prose and may be
    /// reworded without consequence, but an id that silently changes detaches every rule watching for
    /// it - and these particular rules are what monitor the write-restricted-share assumption the
    /// containment boundary depends on. Deliberately spelled out here rather than read back from the
    /// production constants, so that a test compares two independently-stated values instead of
    /// comparing the code against itself.
    /// </remarks>
    private const int ContainmentRefusalEventId = 1010;

    /// <inheritdoc cref="ContainmentRefusalEventId" />
    private const int DocumentRefusedEventId = 1011;

    /// <inheritdoc cref="ContainmentRefusalEventId" />
    private const int DocumentReadFailedEventId = 1012;

    /// <inheritdoc cref="ContainmentRefusalEventId" />
    private const int UnacceptableReferenceShapeEventId = 1014;

    private readonly DirectoryInfo _workspace;
    private readonly string _ingestRoot;
    private readonly string _outsideRoot;
    private readonly InMemoryDocumentRegistry _registry = new();
    private readonly InMemoryIngestAuditLog _auditLog = new();
    private readonly CapturingLogger _logger = new();
    private readonly FixedClock _clock = new(FirstSubmission);
    private readonly RecordingTextLayerExtractor _textLayers = new();

    public DocumentIngestServiceTests()
    {
        _workspace = Directory.CreateTempSubdirectory("cbix-ingest-");
        _ingestRoot = Directory.CreateDirectory(Path.Combine(_workspace.FullName, "ingest")).FullName;
        _outsideRoot = Directory.CreateDirectory(Path.Combine(_workspace.FullName, "outside")).FullName;
    }

    /// <summary>
    /// Removes the workspace, unlinking directory links instead of descending into them.
    /// </summary>
    /// <remarks>
    /// A plain recursive delete fails with "The parameter is incorrect" on a tree containing an NTFS
    /// junction, and - worse in principle - descending into a link would delete the link's target.
    /// These probes deliberately point links at a directory outside the workspace, so a teardown that
    /// followed them would delete things it does not own.
    /// </remarks>
    public void Dispose() => DeleteTree(_workspace);

    private static void DeleteTree(DirectoryInfo directory)
    {
        foreach (DirectoryInfo child in directory.EnumerateDirectories())
        {
            if (child.LinkTarget is not null)
            {
                child.Delete();
            }
            else
            {
                DeleteTree(child);
            }
        }

        foreach (FileInfo file in directory.EnumerateFiles())
        {
            file.Delete();
        }

        directory.Delete();
    }

    // ---------------------------------------------------------------- registration and dedupe

    [Fact]
    public async Task IngestAsync_FirstSubmission_RegistersTheHashOfTheBytesRead()
    {
        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");
        string expectedDigest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(documentPath)));

        DocumentIngestResult result = await Service().IngestAsync(documentPath);

        Assert.True(result.IsNewRegistration);
        Assert.Equal(expectedDigest, result.ContentHash.Value);

        DocumentRegistryEntry entry = Assert.Single(_registry.Entries);
        Assert.Equal($"sha256:{expectedDigest}", entry.DocumentId);
        Assert.Equal("DE_SPECIMEN.pdf", entry.Document.FileName);
        Assert.Equal("application/pdf", entry.Document.MediaType);
        Assert.Equal(FirstSubmission, entry.FirstSeenUtc);
        Assert.Equal(documentPath, entry.Document.Location.LocalPath);

        // The recorded length is the count of bytes actually hashed, so it cannot disagree with the
        // digest sitting beside it in the same row.
        Assert.Equal(new FileInfo(documentPath).Length, entry.ByteLength);

        // Identity reaches the reference the rest of the pipeline keys on, derived - never chosen.
        Assert.Equal(entry.DocumentId, result.Submitted.DocumentId);
    }

    [Fact]
    public async Task IngestAsync_FirstSubmission_WritesARegisteredAuditEntry()
    {
        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");

        DocumentIngestResult result = await Service().IngestAsync(documentPath);

        IngestAuditEntry entry = Assert.Single(_auditLog.Entries);
        Assert.Equal(IngestAuditEventType.DocumentRegistered, entry.EventType);
        Assert.Equal(result.ContentHash, entry.ContentHash);
        Assert.Equal(FirstSubmission, entry.RecordedUtc);
        Assert.Equal(FirstSubmission, entry.FirstRegisteredUtc);
    }

    [Fact]
    public async Task IngestAsync_SameFileTwice_RegistersOnceAndAuditsBoth()
    {
        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");
        DocumentIngestService service = Service();

        DocumentIngestResult first = await service.IngestAsync(documentPath);
        _clock.UtcNow = FirstSubmission.AddHours(6);
        DocumentIngestResult second = await service.IngestAsync(documentPath);

        Assert.False(second.IsNewRegistration);
        Assert.Same(first.Registered, Assert.Single(_registry.Entries));
        Assert.Equal(FirstSubmission, second.Registered.FirstSeenUtc);

        Assert.Equal(2, _auditLog.Entries.Count);
        IngestAuditEntry duplicate = _auditLog.Entries[1];
        Assert.Equal(IngestAuditEventType.DuplicateSubmissionIgnored, duplicate.EventType);
        Assert.Equal(FirstSubmission.AddHours(6), duplicate.RecordedUtc);
        Assert.Equal(FirstSubmission, duplicate.FirstRegisteredUtc);
    }

    [Fact]
    public async Task IngestAsync_SameBytesUnderADifferentName_IsADuplicate()
    {
        // Deduplication is on content, not on file name - and the audit entry is where the changed
        // name survives, since the registry row is never rewritten.
        const string Content = "cross-border instruction";
        string original = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", Content);
        string renamed = WriteDocument(_ingestRoot, "DE_SPECIMEN_copy.pdf", Content);
        DocumentIngestService service = Service();

        await service.IngestAsync(original);
        _clock.UtcNow = FirstSubmission.AddDays(1);
        DocumentIngestResult duplicate = await service.IngestAsync(renamed);

        Assert.False(duplicate.IsNewRegistration);
        Assert.Equal("DE_SPECIMEN.pdf", Assert.Single(_registry.Entries).Document.FileName);
        Assert.Equal("DE_SPECIMEN_copy.pdf", duplicate.Submitted.FileName);
        Assert.Equal("DE_SPECIMEN_copy.pdf", _auditLog.Entries[1].Submitted.FileName);
    }

    [Fact]
    public async Task IngestAsync_DifferentBytes_AreDifferentDocuments()
    {
        DocumentIngestService service = Service();

        await service.IngestAsync(WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "German instruction"));
        DocumentIngestResult second = await service.IngestAsync(WriteDocument(_ingestRoot, "CH_SPECIMEN.pdf", "Swiss instruction"));

        Assert.True(second.IsNewRegistration);
        Assert.Equal(2, _registry.Entries.Count);
        Assert.Equal(2, _auditLog.Entries.Count);
    }

    [Fact]
    public async Task IngestAsync_RelativePath_ResolvesAgainstTheIngestRoot()
    {
        WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");

        DocumentIngestResult result = await Service().IngestAsync("DE_SPECIMEN.pdf");

        Assert.True(result.IsNewRegistration);
    }

    // ---------------------------------------------------------------- text layer preparation

    [Fact]
    public async Task IngestAsync_FirstSubmission_ExtractsTheTextLayerForTheDocumentItRegistered()
    {
        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");

        DocumentIngestResult result = await Service().IngestAsync(documentPath);

        DocumentReference asked = Assert.Single(_textLayers.Calls);

        // Extraction is handed the reference ingest itself minted, so it opens the resolved,
        // contained, round-trip-stable path whose bytes were hashed - not the path the caller typed.
        Assert.Equal(result.Submitted.DocumentId, asked.DocumentId);
        Assert.Equal(documentPath, asked.Location.LocalPath);

        Assert.NotNull(result.TextLayer);
        Assert.Equal(result.ContentHash.Canonical, result.TextLayer.DocumentId);
        Assert.Equal(1, result.TextLayer.PageCount);
    }

    [Fact]
    public async Task IngestAsync_DuplicateSubmission_DoesNotReExtractTheTextLayer()
    {
        // The ordering this asserts is the point: the registry short-circuits before document
        // preparation, so a re-submission stops at "already known" rather than repeating work its
        // run will not use. Free today; a Files API upload and provider tokens once S01-12 joins
        // this step.
        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");
        DocumentIngestService service = Service();

        DocumentIngestResult first = await service.IngestAsync(documentPath);
        DocumentIngestResult duplicate = await service.IngestAsync(documentPath);

        Assert.True(first.IsNewRegistration);
        Assert.False(duplicate.IsNewRegistration);

        Assert.Single(_textLayers.Calls);
        Assert.NotNull(first.TextLayer);
        Assert.Null(duplicate.TextLayer);
    }

    [Fact]
    public async Task IngestAsync_UnreadableDocument_RefusesAfterRegisteringAndAuditing()
    {
        // Pins the stated cost of running preparation last. The refusal reaches the caller as the
        // ingest refusal family, not as whatever the parser raised; and the registry row plus its
        // audit entry survive, because the registration really did happen and a trail that omits it
        // would be a trail that lies.
        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "not really a pdf");
        DocumentIngestService service = new(
            _registry,
            _auditLog,
            new DocumentIngestOptions(_ingestRoot, MaxDocumentBytes),
            new UnreadableTextLayerExtractor(),
            _logger,
            _clock);

        DocumentNotIngestibleException refusal =
            await Assert.ThrowsAsync<DocumentNotIngestibleException>(() => service.IngestAsync(documentPath));

        Assert.Equal(DocumentNotIngestibleReason.Unreadable, refusal.Reason);
        Assert.Single(_registry.Entries);
        Assert.Single(_auditLog.Entries);
        Assert.Equal(IngestAuditEventType.DocumentRegistered, _auditLog.Entries[0].EventType);
    }

    // ---------------------------------------------------------------- containment probes

    [Fact]
    public async Task IngestAsync_RelativeEscapeFromTheRoot_IsRefused()
    {
        WriteDocument(_outsideRoot, "credentials", "AKIA-not-a-document");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(Path.Combine("..", "outside", "credentials")));

        Assert.Equal(IngestRootViolationReason.OutsideIngestRoot, refusal.Reason);
        Assert.Equal(_ingestRoot, refusal.IngestRoot);
        Assert.Equal(Path.Combine(_outsideRoot, "credentials"), refusal.ResolvedPath);
        AssertNothingWasAdmitted();
        AssertRefusalWasLogged();
    }

    [Fact]
    public async Task IngestAsync_AbsolutePathOutsideTheRoot_IsRefused()
    {
        string outsidePath = WriteDocument(_outsideRoot, "credentials", "AKIA-not-a-document");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(outsidePath));

        Assert.Equal(IngestRootViolationReason.OutsideIngestRoot, refusal.Reason);
        Assert.Equal(outsidePath, refusal.ResolvedPath);
        AssertNothingWasAdmitted();
    }

    [Fact]
    public async Task IngestAsync_SiblingDirectorySharingTheRootsPrefix_IsRefused()
    {
        // "…/ingest-evil" starts with "…/ingest": without the trailing separator in the prefix test
        // this submission would be accepted as inside the root.
        string sibling = Directory.CreateDirectory(_ingestRoot + "-evil").FullName;
        string documentPath = WriteDocument(sibling, "DE_SPECIMEN.pdf", "cross-border instruction");

        await Assert.ThrowsAsync<IngestRootViolationException>(() => Service().IngestAsync(documentPath));

        AssertNothingWasAdmitted();
    }

    [Theory]
    [InlineData(@"\\attacker.example\share\payload.pdf")]
    [InlineData(@"//attacker.example/share/payload.pdf")]
    [InlineData(@"\\?\C:\Windows\System32\config\SAM")]
    [InlineData(@"\\.\PhysicalDrive0")]
    public async Task IngestAsync_UncOrDevicePath_IsRefusedWithoutTouchingTheFileSystem(string documentPath)
    {
        // The refusal must be decided on the string. Resolving a UNC path is itself the harm: Windows
        // authenticates outbound to the named host (measured at ~11s of block plus an NTLM exchange)
        // and reports the failure as an IOException, which a caller reads as transient and retries.
        // Asserting the classification - and that the resolved path is still the submitted string,
        // untouched by normalisation - is the stable form of "no I/O happened"; a timing assertion
        // would be flaky on a loaded CI runner.
        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(documentPath));

        Assert.Equal(IngestRootViolationReason.UncOrDevicePath, refusal.Reason);
        Assert.Equal(documentPath, refusal.SubmittedPath);
        Assert.Equal(documentPath, refusal.ResolvedPath);
        AssertNothingWasAdmitted();
        AssertRefusalWasLogged();
    }

    [SkippableFact]
    public async Task IngestAsync_DirectoryJunctionTunnellingOutOfTheRoot_IsRefused()
    {
        // The measured escape: mklink /J needs no elevation, the leaf inside the junction looks
        // entirely ordinary, and leaf-only resolution reads bytes from outside the root while
        // recording the in-root path - provenance stating something untrue.
        Skip.IfNot(OperatingSystem.IsWindows(), "Directory junctions are a Windows NTFS feature.");

        string linkPath = Path.Combine(_ingestRoot, "tunnel");
        Skip.IfNot(TryCreateJunction(linkPath, _outsideRoot), "This host refused to create a directory junction.");

        WriteDocument(_outsideRoot, "credentials.pdf", "AKIA-not-a-document");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(Path.Combine(linkPath, "credentials.pdf")));

        Assert.Equal(IngestRootViolationReason.OutsideIngestRoot, refusal.Reason);
        Assert.Equal(Path.Combine(_outsideRoot, "credentials.pdf"), refusal.ResolvedPath);
        AssertNothingWasAdmitted();
    }

    [SkippableFact]
    public async Task IngestAsync_SymlinkedDirectoryTunnellingOutOfTheRoot_IsRefused()
    {
        // The portable form of the same tunnel - this is the leg that runs on Linux CI.
        string linkPath = Path.Combine(_ingestRoot, "tunnel");
        Skip.IfNot(TryCreateDirectorySymbolicLink(linkPath, _outsideRoot), "This host refused to create a directory symbolic link.");

        WriteDocument(_outsideRoot, "credentials.pdf", "AKIA-not-a-document");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(Path.Combine(linkPath, "credentials.pdf")));

        Assert.Equal(IngestRootViolationReason.OutsideIngestRoot, refusal.Reason);
        Assert.Equal(Path.Combine(_outsideRoot, "credentials.pdf"), refusal.ResolvedPath);
        AssertNothingWasAdmitted();
    }

    [SkippableFact]
    public async Task IngestAsync_HardLinkToAFileOutsideTheRoot_IsRefused()
    {
        // A hard link is a second, equal name for one file: there is no link to resolve and nothing
        // in the path to notice, so every path-based check passes it. Only the link count on the
        // opened handle sees it.
        string outsidePath = WriteDocument(_outsideRoot, "credentials", "AKIA-not-a-document");
        string linkPath = Path.Combine(_ingestRoot, "innocent.pdf");

        Skip.IfNot(TryCreateHardLink(linkPath, outsidePath), "This host refused to create a hard link.");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(linkPath));

        Assert.Equal(IngestRootViolationReason.MultiplyLinkedFile, refusal.Reason);
        AssertNothingWasAdmitted();
        AssertRefusalWasLogged();
    }

    [SkippableFact]
    public async Task IngestAsync_HardLinkEntirelyInsideTheRoot_IsAlsoRefused()
    {
        // Deliberately recorded as a false positive we accept. The link count cannot tell an escape
        // from a benign second name, so a hard-linked file inside the root is refused too. That is
        // the right trade for a landing zone (documents are copied in, not hard-linked), and it is a
        // test rather than a comment so that changing the rule has to be a decision.
        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");
        string linkPath = Path.Combine(_ingestRoot, "DE_SPECIMEN_link.pdf");

        Skip.IfNot(TryCreateHardLink(linkPath, documentPath), "This host refused to create a hard link.");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(linkPath));

        Assert.Equal(IngestRootViolationReason.MultiplyLinkedFile, refusal.Reason);
    }

    [SkippableFact]
    public async Task IngestAsync_SymbolicLinkLeafEscapingTheRoot_IsRefused()
    {
        string outsidePath = WriteDocument(_outsideRoot, "credentials", "AKIA-not-a-document");
        string linkPath = Path.Combine(_ingestRoot, "innocent.pdf");

        Skip.IfNot(TryCreateFileSymbolicLink(linkPath, outsidePath), "This host refused to create a symbolic link.");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(linkPath));

        Assert.Equal(IngestRootViolationReason.OutsideIngestRoot, refusal.Reason);
        Assert.Equal(linkPath, refusal.SubmittedPath);
        Assert.Equal(outsidePath, refusal.ResolvedPath);
        AssertNothingWasAdmitted();
    }

    [SkippableFact]
    public async Task IngestAsync_SymbolicLinkInsideTheRoot_IsAccepted()
    {
        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");
        string linkPath = Path.Combine(_ingestRoot, "latest.pdf");

        Skip.IfNot(TryCreateFileSymbolicLink(linkPath, documentPath), "This host refused to create a symbolic link.");

        // Link resolution must not degrade into a blanket refusal: a "latest" link inside the share
        // is ordinary operational practice, and the document it names is inside the root.
        DocumentIngestResult result = await Service().IngestAsync(linkPath);

        Assert.True(result.IsNewRegistration);
        Assert.Equal("DE_SPECIMEN.pdf", result.Submitted.FileName);
    }

    [SkippableFact]
    public async Task IngestAsync_AbsolutePathUnderASymlinkedRoot_IsAccepted()
    {
        // The false-refusal case that a fully-resolved root and a leaf-only-resolved candidate
        // produce: every absolute submission under a linked root fails to prefix-match and is
        // refused, which is an outage rather than a defence. Both sides are resolved the same way.
        string linkedRoot = Path.Combine(_workspace.FullName, "linked-root");
        Skip.IfNot(TryCreateDirectorySymbolicLink(linkedRoot, _ingestRoot), "This host refused to create a directory symbolic link.");

        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");
        DocumentIngestService service = Service(ingestRoot: linkedRoot);

        // Submitted through the real root, and through the linked one: both must be accepted, and
        // both must resolve to the same identity.
        DocumentIngestResult viaRealPath = await service.IngestAsync(documentPath);
        DocumentIngestResult viaLink = await service.IngestAsync(Path.Combine(linkedRoot, "DE_SPECIMEN.pdf"));

        Assert.True(viaRealPath.IsNewRegistration);
        Assert.False(viaLink.IsNewRegistration);
        Assert.Single(_registry.Entries);
    }

    [SkippableFact]
    public async Task IngestAsync_TunnelBeneathASymlinkedRoot_IsStillRefused()
    {
        // Stage 1 accepts a path under either spelling of the root, which is what stops a linked root
        // refusing everything beneath it. This proves that leniency is backstopped: a tunnel reached
        // through the configured spelling passes stage 1 and is refused by stage 2, which resolves.
        string linkedRoot = Path.Combine(_workspace.FullName, "linked-root");
        Skip.IfNot(TryCreateDirectorySymbolicLink(linkedRoot, _ingestRoot), "This host refused to create a directory symbolic link.");

        string tunnel = Path.Combine(_ingestRoot, "tunnel");
        Skip.IfNot(TryCreateDirectorySymbolicLink(tunnel, _outsideRoot), "This host refused to create a directory symbolic link.");

        WriteDocument(_outsideRoot, "credentials.pdf", "AKIA-not-a-document");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service(ingestRoot: linkedRoot).IngestAsync(Path.Combine(linkedRoot, "tunnel", "credentials.pdf")));

        Assert.Equal(IngestRootViolationReason.OutsideIngestRoot, refusal.Reason);
        Assert.Equal(Path.Combine(_outsideRoot, "credentials.pdf"), refusal.ResolvedPath);
        AssertNothingWasAdmitted();
    }

    [Fact]
    public async Task IngestAsync_PercentEncodedDotSegmentInThePath_IsRefusedAsUnrepresentable()
    {
        // Measured: new Uri(@"C:\ingest\%2e%2e\evil.pdf").LocalPath is "C:\evil.pdf". The bytes hashed
        // were the real file inside the root; the location recorded named a different file outside
        // it. A percent sign is an ordinary character in a file name, so this is a drop share doing
        // something normal, not an exotic attack.
        string encodedDirectory = Directory.CreateDirectory(Path.Combine(_ingestRoot, "%2e%2e")).FullName;
        string documentPath = WriteDocument(encodedDirectory, "evil.pdf", "cross-border instruction");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(documentPath));

        Assert.Equal(IngestRootViolationReason.UnrepresentableLocation, refusal.Reason);
        AssertNothingWasAdmitted();
        AssertRefusalWasLogged();
    }

    [Fact]
    public async Task IngestAsync_PercentEncodedCharacterInTheLeafName_IsRefusedAsUnrepresentable()
    {
        // Measured: new Uri(@"C:\ingest\annex%41.pdf").LocalPath is "C:\ingest\annexA.pdf" - a file
        // that does not exist. The digest would have described one file and the provenance another.
        string documentPath = WriteDocument(_ingestRoot, "annex%41.pdf", "cross-border instruction");

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(documentPath));

        Assert.Equal(IngestRootViolationReason.UnrepresentableLocation, refusal.Reason);
        Assert.Equal(documentPath, refusal.ResolvedPath);
        AssertNothingWasAdmitted();
    }

    [Fact]
    public async Task IngestAsync_RecordedLocation_NamesTheFileWhoseBytesWereHashed()
    {
        // The contract S01-05 uploads from: Location.LocalPath is resolved, contained, and renders
        // back to exactly the hashed path. The name carries a space and parentheses so the URI layer
        // has something to escape - an all-alphanumeric name would satisfy this assertion no matter
        // how the location was built.
        string documentPath = WriteDocument(_ingestRoot, "DE SPECIMEN (v2).pdf", "cross-border instruction");

        DocumentIngestResult result = await Service().IngestAsync(documentPath);

        Assert.Equal(documentPath, result.Submitted.Location.LocalPath);
        Assert.StartsWith(_ingestRoot + Path.DirectorySeparatorChar, result.Submitted.Location.LocalPath, StringComparison.Ordinal);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(result.Submitted.Location.LocalPath))),
            result.ContentHash.Value);
    }

    [Fact]
    public async Task IngestAsync_RefusalOfAHostilePath_NeitherLogsNorReportsItVerbatim()
    {
        // Log injection and message disclosure, pinned. The CR/LF forges a second log entry; U+202E
        // (RIGHT-TO-LEFT OVERRIDE) makes a name render as something else entirely to whoever reads
        // the alert. A UNC prefix is used so the refusal is decided on the raw string, which keeps
        // this test about sanitisation rather than about path resolution.
        const string Hostile = "\\\\attacker.example\\share\\eve\r\nfake\u202Efdp.exe";

        IngestRootViolationException refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => Service().IngestAsync(Hostile));

        (LogLevel Level, int EventId, string Message) entry = Assert.Single(_logger.Entries);
        Assert.Equal(ContainmentRefusalEventId, entry.EventId);
        string logged = entry.Message;

        Assert.DoesNotContain('\r', logged);
        Assert.DoesNotContain('\n', logged);
        Assert.DoesNotContain('\u202E', logged);

        // Replaced, not dropped: an operator has to see that something was removed.
        Assert.Contains('\uFFFD', logged);

        // The exception message is reason-derived, so nothing attacker-supplied travels in it.
        Assert.DoesNotContain("attacker", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("fdp.exe", refusal.Message, StringComparison.Ordinal);

        // The unmodified path stays available on the typed property, for code rather than for logs.
        Assert.Equal(Hostile, refusal.SubmittedPath);
    }

    [Fact]
    public async Task IngestAsync_RefusalOfAPathWithALoneSurrogate_ReplacesItInTheLog()
    {
        // A lone surrogate is not a character: it is half of one. .NET strings hold it happily
        // because they are UTF-16 code units rather than text, and it reaches a log sink as invalid
        // UTF-16 - which at worst breaks that sink's encoder and turns a security event into a lost
        // one. The hazard belongs to the code unit, not to any truncation boundary, so it is
        // replaced wherever it appears.
        const string LoneSurrogate = "\\\\attacker.example\\share\\lone\uD800.pdf";

        await Assert.ThrowsAsync<IngestRootViolationException>(() => Service().IngestAsync(LoneSurrogate));

        (LogLevel Level, int EventId, string Message) entry = Assert.Single(_logger.Entries);
        Assert.Equal(ContainmentRefusalEventId, entry.EventId);
        string logged = entry.Message;

        Assert.DoesNotContain('\uD800', logged);
        Assert.Contains('\uFFFD', logged);
    }

    [Fact]
    public async Task IngestAsync_RefusalOfAPathWithAWellFormedSurrogatePair_ReplacesBothHalves()
    {
        // Pins the accepted cost of replacing on the category rather than on pairing: a legitimate
        // astral-plane name - here U+1F4C4 PAGE FACING UP - renders as two replacement characters.
        // Deliberate. The alternative is pair-aware scanning that admits exactly the code units the
        // sanitiser exists to neutralise, in exchange for prettier rendering of a path that is being
        // logged precisely because its submission was refused.
        const string AstralPlane = "\\\\attacker.example\\share\\report\U0001F4C4.pdf";

        await Assert.ThrowsAsync<IngestRootViolationException>(() => Service().IngestAsync(AstralPlane));

        (LogLevel Level, int EventId, string Message) entry = Assert.Single(_logger.Entries);
        Assert.Equal(ContainmentRefusalEventId, entry.EventId);
        string logged = entry.Message;

        Assert.DoesNotContain('\uD83D', logged);
        Assert.DoesNotContain('\uDCC4', logged);
        Assert.Contains("\uFFFD\uFFFD", logged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('\u2028')] // LINE SEPARATOR
    [InlineData('\u2029')] // PARAGRAPH SEPARATOR
    public async Task IngestAsync_RefusalOfAPathWithAUnicodeLineBreak_ReplacesItInTheLog(char separator)
    {
        // The CR/LF attack in a costume that char.IsControl does not recognise. U+2028 and U+2029 end
        // a line in JavaScript, in a browser rendering a log viewer, and in any NDJSON consumer that
        // splits on Unicode line boundaries - so the forged entry lands exactly as it would with a
        // bare newline. Measured before this was fixed: the payload below travelled into the log
        // intact.
        string forged = $"\\\\attacker.example\\share\\report{separator}INFO: admin authorised.pdf";

        await Assert.ThrowsAsync<IngestRootViolationException>(() => Service().IngestAsync(forged));

        string logged = Assert.Single(_logger.Entries).Message;

        Assert.DoesNotContain(separator, logged);
        Assert.Contains('\uFFFD', logged);

        // The text still travels - it is evidence - but it can no longer start its own line.
        Assert.Contains("INFO: admin authorised.pdf", logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IngestAsync_RefusalOfAnOverlongPath_MarksTheLoggedPathAsTruncated()
    {
        // Without the marker a truncated path reads as a complete one, and an operator compares it
        // against the share, finds nothing, and concludes the alert was noise.
        string overlong = "\\\\attacker.example\\share\\" + new string('a', 600) + ".pdf";

        await Assert.ThrowsAsync<IngestRootViolationException>(() => Service().IngestAsync(overlong));

        string logged = Assert.Single(_logger.Entries).Message;

        Assert.Contains("...[truncated]", logged, StringComparison.Ordinal);

        // Bounded, not merely annotated: an unbounded path in a log line is a flooding hazard on its
        // own.
        Assert.DoesNotContain(new string('a', 600), logged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IngestAsync_LeafNameThatCanNeverConstructAReference_IsRefusedAndLogged()
    {
        // This was the one refusal class in this service that emitted no structured event: the
        // DocumentReference constructor's ArgumentException simply flew, so the SIEM saw nothing.
        // The inputs it rejects are the deliberately hostile ones, which made it the worst possible
        // gap to leave - the refusal existed, but nobody was told about it.
        //
        // "annex..evil.pdf" is a legal file name on every host and is refused by the reference's
        // rules for containing "..", so the probe is exercised without the test depending on a name
        // the file system might decline to create.
        string documentPath = WriteDocument(_ingestRoot, "annex..evil.pdf", "cross-border instruction");

        await Assert.ThrowsAsync<ArgumentException>(() => Service().IngestAsync(documentPath));

        (LogLevel Level, int EventId, string Message) entry = Assert.Single(_logger.Entries);

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("fileName", entry.Message, StringComparison.Ordinal);

        // Refused before a byte was read, so nothing is registered and no document-scoped audit
        // entry exists to write - the security signal is the log event, which is the point.
        AssertNothingWasAdmitted();
    }

    [Fact]
    public async Task IngestAsync_LeafNameCarryingAFormatCharacter_IsNotRenderedRawIntoItsOwnRefusalEvent()
    {
        // The refusal and the sanitiser have to compose: a name rejected for carrying U+202E is
        // exactly the string that must not reach the log that reports the rejection.
        string documentPath = WriteDocument(_ingestRoot, "annex\u202Efdp.pdf", "cross-border instruction");

        await Assert.ThrowsAsync<ArgumentException>(() => Service().IngestAsync(documentPath));

        (LogLevel Level, int EventId, string Message) entry = Assert.Single(_logger.Entries);
        Assert.Equal(UnacceptableReferenceShapeEventId, entry.EventId);
        string logged = entry.Message;

        Assert.DoesNotContain('\u202E', logged);
        Assert.Contains('\uFFFD', logged);
    }

    // ---------------------------------------------------------------- not-a-document outcomes

    [Fact]
    public async Task IngestAsync_TheIngestRootItself_IsRefusedAsNotAFile()
    {
        DocumentNotIngestibleException refusal = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => Service().IngestAsync(_ingestRoot));

        // Not a containment violation: submitting the root is a caller that meant to enumerate, and
        // classifying it as an escape attempt would put noise in the security signal.
        Assert.Equal(DocumentNotIngestibleReason.NotAFile, refusal.Reason);
        AssertNothingWasAdmitted();
    }

    [Fact]
    public async Task IngestAsync_ADirectoryInsideTheRoot_IsRefusedAsNotAFile()
    {
        string directory = Directory.CreateDirectory(Path.Combine(_ingestRoot, "batch")).FullName;

        DocumentNotIngestibleException refusal = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => Service().IngestAsync(directory));

        Assert.Equal(DocumentNotIngestibleReason.NotAFile, refusal.Reason);
    }

    [Fact]
    public async Task IngestAsync_EmptyFile_IsRefusedAsEmpty()
    {
        // Mirrors ck_document_registry_byte_length. SHA-256 of nothing is a valid digest, which is
        // exactly the problem: admitted, it becomes one identity every future empty file collides
        // with.
        string emptyPath = WriteDocument(_ingestRoot, "empty.pdf", string.Empty);

        DocumentNotIngestibleException refusal = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => Service().IngestAsync(emptyPath));

        Assert.Equal(DocumentNotIngestibleReason.Empty, refusal.Reason);
        Assert.Equal(0, refusal.ByteLength);
        AssertNothingWasAdmitted();

        // Its own event id, distinct from the containment one: a data-quality refusal and a security
        // refusal warrant different operational responses, so an alert rule has to be able to tell
        // them apart without parsing message text.
        (LogLevel Level, int EventId, string Message) logged = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Equal(DocumentRefusedEventId, logged.EventId);
        Assert.NotEqual(ContainmentRefusalEventId, logged.EventId);
    }

    [Fact]
    public async Task IngestAsync_FileOverTheConfiguredMaximum_IsRefusedWhileReading()
    {
        string oversizedPath = Path.Combine(_ingestRoot, "oversized.pdf");
        File.WriteAllBytes(oversizedPath, RandomNumberGenerator.GetBytes((int)MaxDocumentBytes + 1));

        DocumentNotIngestibleException refusal = await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => Service().IngestAsync(oversizedPath));

        Assert.Equal(DocumentNotIngestibleReason.TooLarge, refusal.Reason);

        // The read was abandoned at the limit rather than run to completion: an unbounded read is an
        // unbounded upload and an unbounded provider bill.
        Assert.True(
            refusal.ByteLength <= MaxDocumentBytes + ReadChunkAllowance,
            $"The read continued to {refusal.ByteLength} bytes past a {MaxDocumentBytes}-byte limit.");
        AssertNothingWasAdmitted();
    }

    [Fact]
    public async Task IngestAsync_FileExactlyAtTheConfiguredMaximum_IsAccepted()
    {
        // The boundary is inclusive; an off-by-one here would refuse a legitimate document.
        string documentPath = Path.Combine(_ingestRoot, "at-limit.pdf");
        File.WriteAllBytes(documentPath, RandomNumberGenerator.GetBytes((int)MaxDocumentBytes));

        DocumentIngestResult result = await Service().IngestAsync(documentPath);

        Assert.Equal(MaxDocumentBytes, result.Registered.ByteLength);
    }

    [Fact]
    public async Task IngestAsync_MissingFile_ThrowsAndLogsAStructuredEvent()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Service().IngestAsync("absent.pdf"));

        // A failure this service did not decide still has to leave a trace: the exception type alone,
        // because the BCL message embeds the path unsanitised.
        (LogLevel Level, int EventId, string Message) logged = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, logged.Level);
        Assert.Equal(DocumentReadFailedEventId, logged.EventId);
        Assert.Contains(nameof(FileNotFoundException), logged.Message, StringComparison.Ordinal);
        AssertNothingWasAdmitted();
    }

    [Fact]
    public async Task IngestAsync_LeafNameThatCannotConstructAReference_IsRefusedBeforeReading()
    {
        // DocumentReference rejects a control character in a display name. Checking the name before
        // opening means a document that can never be accepted does not first pay for a full read and
        // hash; the reference constructor stays the backstop.
        // The control character is written as an escape, never pasted in: an invisible character in
        // a source file is unreviewable, and this one was silently stripped in transit once already
        // while this test was being written.
        string documentPath = Path.Combine(_ingestRoot, "annex" + (char)0x07 + "bell.pdf");

        // The file deliberately does not exist: if the name check did not run first, the failure
        // would be FileNotFoundException from the open rather than a rejection of the name.
        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => Service().IngestAsync(documentPath));

        Assert.Equal("fileName", error.ParamName);
        AssertNothingWasAdmitted();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IngestAsync_BlankPath_Throws(string documentPath)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().IngestAsync(documentPath));
    }

    [Fact]
    public async Task IngestAsync_AuditFailure_IsNotSwallowedAndLeavesTheRegistryRow()
    {
        // Fail closed: an ingest step that continued after losing its audit record would publish a
        // value nobody can account for. The registry row surviving is the deliberate half of that
        // trade - re-submitting does NOT restore the lost registration event, it records a duplicate.
        string documentPath = WriteDocument(_ingestRoot, "DE_SPECIMEN.pdf", "cross-border instruction");
        DocumentIngestService service = new(
            _registry,
            new ThrowingAuditLog(),
            new DocumentIngestOptions(_ingestRoot, MaxDocumentBytes),
            _textLayers,
            NullLogger<DocumentIngestService>.Instance,
            _clock);

        await Assert.ThrowsAsync<IOException>(() => service.IngestAsync(documentPath));

        Assert.Single(_registry.Entries);
    }

    // ---------------------------------------------------------------- construction

    [Fact]
    public void Constructor_NullCollaborators_Throw()
    {
        DocumentIngestOptions options = new(_ingestRoot, MaxDocumentBytes);

        Assert.Throws<ArgumentNullException>(
            () => new DocumentIngestService(null!, _auditLog, options, _textLayers, NullLogger<DocumentIngestService>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new DocumentIngestService(_registry, null!, options, _textLayers, NullLogger<DocumentIngestService>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new DocumentIngestService(_registry, _auditLog, null!, _textLayers, NullLogger<DocumentIngestService>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new DocumentIngestService(_registry, _auditLog, options, null!, NullLogger<DocumentIngestService>.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new DocumentIngestService(_registry, _auditLog, options, _textLayers, null!));
    }

    [Fact]
    public void Constructor_RootThatDoesNotExist_IsAcceptedAndFailsPerDocument()
    {
        // A root that is not mounted yet is an operational state, not a configuration error: the
        // service constructs, and the first submission reports a missing directory.
        string absentRoot = Path.Combine(_workspace.FullName, "not-mounted-yet");

        DocumentIngestService service = Service(ingestRoot: absentRoot);

        Assert.NotNull(service);
    }

    private const int ReadChunkAllowance = 81920;

    // ---------------------------------------------------------------- helpers

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath) =>
        TryLinkOperation(() => File.CreateSymbolicLink(linkPath, targetPath));

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath) =>
        TryLinkOperation(() => Directory.CreateSymbolicLink(linkPath, targetPath));

    /// <summary>
    /// Creates a hard link, the one link kind .NET has no API for: <c>CreateHardLinkW</c> on Windows,
    /// <c>link(2)</c> on Unix. Both are unprivileged operations, which is precisely why the control
    /// they defeat has to exist.
    /// </summary>
    private static bool TryCreateHardLink(string linkPath, string targetPath) =>
        TryLinkOperation(() =>
        {
            bool created = OperatingSystem.IsWindows()
                ? CreateHardLinkW(linkPath, targetPath, IntPtr.Zero)
                : link(targetPath, linkPath) == 0;

            if (!created)
            {
                throw new IOException($"Creating a hard link failed with error {Marshal.GetLastPInvokeError()}.");
            }
        });

    /// <summary>
    /// Creates an NTFS directory junction. There is no .NET API and no elevation requirement -
    /// <c>mklink /J</c> is available to any user, which is what makes the junction tunnel a realistic
    /// escape rather than a theoretical one.
    /// </summary>
    private static bool TryCreateJunction(string linkPath, string targetPath) =>
        TryLinkOperation(() =>
        {
            using Process? process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", linkPath, targetPath])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit();

            if (process?.ExitCode != 0 || !Directory.Exists(linkPath))
            {
                throw new IOException("mklink /J did not create the junction.");
            }
        });

    private static bool TryLinkOperation(Action operation)
    {
        try
        {
            operation();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string WriteDocument(string directory, string fileName, string content)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

        return path;
    }

    private DocumentIngestService Service(string? ingestRoot = null) =>
        new(_registry,
            _auditLog,
            new DocumentIngestOptions(ingestRoot ?? _ingestRoot, MaxDocumentBytes),
            _textLayers,
            _logger,
            _clock);

    private void AssertNothingWasAdmitted()
    {
        Assert.Empty(_registry.Entries);
        Assert.Empty(_auditLog.Entries);
    }

    private void AssertRefusalWasLogged()
    {
        // A refusal that only throws is invisible until something catches it; the security event is
        // what the SIEM sees, and it is what monitors the write-restricted-share assumption.
        //
        // The event id is asserted, not just the level. An alert rule keys on the id, so the id is
        // the log's actual contract - message wording is prose that may legitimately be reworded,
        // while a renumbering silently detaches every rule watching for it. Every caller of this
        // helper is a containment probe, which is why one id is right here.
        Assert.Contains(
            _logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.EventId == ContainmentRefusalEventId);
    }

    /// <summary>A clock the test moves by hand, so "first seen" and "recorded at" are exact values rather than "about now".</summary>
    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    /// <summary>
    /// A text-layer extractor that records what it was asked for and answers with a fixed page.
    /// </summary>
    /// <remarks>
    /// A stub, not the real PDFPig extractor, for two reasons. The documents these tests write are
    /// short strings rather than valid PDFs - the gate's subject is bytes and paths, not page
    /// structure - and the properties under test here are about <em>how often</em> and <em>with
    /// what</em> extraction is invoked, which only a seam can answer. The PDFPig implementation has
    /// its own tests against the real specimen.
    /// </remarks>
    private sealed class RecordingTextLayerExtractor : ITextLayerExtractor
    {
        private readonly List<DocumentReference> _calls = [];

        public IReadOnlyList<DocumentReference> Calls => _calls;

        public Task<TextLayer> ExtractAsync(DocumentReference document, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);

            _calls.Add(document);

            return Task.FromResult(new TextLayer(document.DocumentId, ["extracted page one"]));
        }
    }

    /// <summary>An extractor standing in for a file the PDF parser cannot read.</summary>
    private sealed class UnreadableTextLayerExtractor : ITextLayerExtractor
    {
        public Task<TextLayer> ExtractAsync(DocumentReference document, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);

            throw new DocumentNotIngestibleException(
                DocumentNotIngestibleReason.Unreadable,
                document.Location.LocalPath,
                byteLength: null,
                new InvalidOperationException("the parser gave up"));
        }
    }

    /// <summary>An audit sink that cannot record, standing in for a durable store that is down.</summary>
    private sealed class ThrowingAuditLog : IIngestAuditLog
    {
        public Task RecordAsync(IngestAuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new IOException("The audit store is unavailable.");
    }

    /// <summary>Captures log entries so the refusal signal can be asserted rather than assumed.</summary>
    private sealed class CapturingLogger : ILogger<DocumentIngestService>
    {
        private readonly List<(LogLevel Level, int EventId, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, int EventId, string Message)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            _entries.Add((logLevel, eventId.Id, formatter(state, exception)));
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string linkName, string existingFileName, IntPtr securityAttributes);

#pragma warning disable IDE1006 // The libc entry point is named in C, not in C#.
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int link(string existingPath, string newPath);
#pragma warning restore IDE1006
}
