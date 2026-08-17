using System.Security.Cryptography;

using Cbix.Bdd.Support;
using Cbix.Core.Ingest;

using Microsoft.Extensions.Logging.Abstractions;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/IngestContentHashAndDedupe.feature</c> (story S01-10).
/// <para>
/// The scenarios run against the real DE specimen on disk, read-only, through the real
/// <see cref="DocumentIngestService"/> with the in-memory registry and audit log. Nothing here
/// stubs the hashing: the expected digest is recomputed independently from the file's bytes, so the
/// assertion compares the service's answer against SHA-256 rather than against itself.
/// </para>
/// <para>
/// The clock is the system one on purpose. A duplicate's audit entry is asserted on ordering
/// (first-registered no later than recorded) and on identity, not on a literal instant, so the
/// scenarios need no injected time - and the timestamps they do assert are the ones production
/// would produce.
/// </para>
/// </summary>
[Binding]
public sealed class IngestContentHashAndDedupeSteps(IngestContentHashAndDedupeState state)
{
    /// <summary>The specimen named by the story's Gherkin, relative to the repository's data directory.</summary>
    private const string SpecimenFileName = RepositoryLayout.DeSpecimenFileName;

    [Given("the DE specimen has not been submitted before")]
    public void GivenTheDeSpecimenHasNotBeenSubmittedBefore()
    {
        string ingestRoot = RepositoryLayout.DataDirectory();
        string specimenPath = Path.Combine(ingestRoot, SpecimenFileName);

        Assert.True(File.Exists(specimenPath), $"The DE specimen is missing from the repository at '{specimenPath}'.");

        // One options instance shared by the gate and the extractor, exactly as the composition root
        // will do it: the size bound the extractor re-applies to its reopened handle has to be the
        // same bound the gate counted against, and sharing the object is what makes that structural.
        DocumentIngestOptions options = new(ingestRoot, DocumentIngestOptions.ClaudeFilesApiLimitBytes);

        state.SpecimenPath = specimenPath;
        state.Service = new DocumentIngestService(
            state.Registry,
            state.AuditLog,
            options,
            // The real extractor, not a stub: these scenarios submit the real specimen, so the
            // production preparation step runs on it exactly as it would in a run. Text-layer
            // behaviour itself is asserted by S01-11's scenarios; here it simply must not be
            // simulated away.
            new PdfPigTextLayerExtractor(options, NullLogger<PdfPigTextLayerExtractor>.Instance),
            NullLogger<DocumentIngestService>.Instance);

        Assert.Empty(state.Registry.Entries);
        Assert.Empty(state.AuditLog.Entries);
    }

    [Given("the DE specimen was already registered with its content hash")]
    public async Task GivenTheDeSpecimenWasAlreadyRegisteredWithItsContentHash()
    {
        GivenTheDeSpecimenHasNotBeenSubmittedBefore();

        state.FirstResult = await IngestSpecimenAsync();

        Assert.True(state.FirstResult.IsNewRegistration, "The first submission should have created the registry record.");
        Assert.Single(state.Registry.Entries);
    }

    [Given("the DE specimen is in the configured ingest root")]
    public void GivenTheDeSpecimenIsInTheConfiguredIngestRoot()
    {
        GivenTheDeSpecimenHasNotBeenSubmittedBefore();
    }

    [When("the ingest executor processes it")]
    public async Task WhenTheIngestExecutorProcessesIt()
    {
        state.FirstResult = await IngestSpecimenAsync();
    }

    [When("the same file is submitted again unchanged")]
    public async Task WhenTheSameFileIsSubmittedAgainUnchanged()
    {
        state.DuplicateResult = await IngestSpecimenAsync();
    }

    [When("a path that resolves outside the ingest root is submitted")]
    public async Task WhenAPathThatResolvesOutsideTheIngestRootIsSubmitted()
    {
        DocumentIngestService service = RequireService();

        // A real file that really exists, one directory above the root: the submission has to be
        // refused for where it resolves to, not for being absent, or the scenario would pass
        // against a service with no containment check at all. The existence assertion is what keeps
        // that true - if global.json were ever moved or renamed, this scenario would silently
        // degrade into testing that a missing file is refused, which proves nothing.
        string escapingPath = Path.Combine("..", "global.json");
        string escapingTarget = Path.GetFullPath(escapingPath, Path.GetDirectoryName(state.SpecimenPath!)!);

        Assert.True(
            File.Exists(escapingTarget),
            $"The escape target '{escapingTarget}' does not exist, so this scenario would no longer prove containment.");

        state.Refusal = await Assert.ThrowsAsync<IngestRootViolationException>(
            () => service.IngestAsync(escapingPath));
    }

    [Then("the submission is refused as outside the ingest root")]
    public void ThenTheSubmissionIsRefusedAsOutsideTheIngestRoot()
    {
        IngestRootViolationException refusal = state.Refusal
            ?? throw new InvalidOperationException("The When step did not capture a refusal.");

        Assert.Equal(IngestRootViolationReason.OutsideIngestRoot, refusal.Reason);
        Assert.Equal("global.json", Path.GetFileName(refusal.ResolvedPath));
        Assert.Equal("data", Path.GetFileName(refusal.IngestRoot));
        Assert.DoesNotContain(refusal.IngestRoot + Path.DirectorySeparatorChar, refusal.ResolvedPath, StringComparison.Ordinal);

        // The message is reason-derived and path-free: refusal messages travel into logs, and the
        // paths in them are input somebody else chose.
        Assert.DoesNotContain(refusal.ResolvedPath, refusal.Message, StringComparison.Ordinal);
    }

    [Then("no registry record is created")]
    public void ThenNoRegistryRecordIsCreated()
    {
        Assert.Empty(state.Registry.Entries);
    }

    [Then("no audit log entry is written")]
    public void ThenNoAuditLogEntryIsWritten()
    {
        // The refusal happens before any byte is read, so there is no document to write a
        // document-scoped audit entry about; the security signal belongs in operational logging.
        Assert.Empty(state.AuditLog.Entries);
    }

    [Then("a content hash is computed")]
    public void ThenAContentHashIsComputed()
    {
        DocumentIngestResult result = RequireFirstResult();
        string specimenPath = state.SpecimenPath
            ?? throw new InvalidOperationException("The Given step did not resolve the specimen path.");

        // Recomputed here from the raw bytes, deliberately without going through the production
        // type: an assertion built on the code under test would pass no matter what it computed.
        string expected = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(specimenPath)));

        Assert.Equal(ContentHash.Sha256Algorithm, result.ContentHash.Algorithm);
        Assert.Equal(expected, result.ContentHash.Value);
        Assert.Equal($"{ContentHash.Sha256Algorithm}:{expected}", result.ContentHash.Canonical);
    }

    [Then("a new registry record is created with that hash")]
    public void ThenANewRegistryRecordIsCreatedWithThatHash()
    {
        DocumentIngestResult result = RequireFirstResult();

        Assert.True(result.IsNewRegistration, "The submission did not create a registry record.");

        DocumentRegistryEntry entry = Assert.Single(state.Registry.Entries);

        Assert.Equal(result.ContentHash.Canonical, entry.DocumentId);
        Assert.Equal(result.ContentHash, entry.ContentHash);
        Assert.Equal(SpecimenFileName, entry.Document.FileName);

        // The registry is the sole authority that derives an identity from the bytes it read, so the
        // reference every downstream stage keys on must carry that derived identity - not a name the
        // submission chose.
        Assert.Equal(entry.DocumentId, result.Submitted.DocumentId);
    }

    [Then("the ingest executor makes no new registry record")]
    public void ThenTheIngestExecutorMakesNoNewRegistryRecord()
    {
        DocumentIngestResult first = RequireFirstResult();
        DocumentIngestResult duplicate = RequireDuplicateResult();

        Assert.False(duplicate.IsNewRegistration, "The duplicate submission was treated as a new registration.");

        DocumentRegistryEntry entry = Assert.Single(state.Registry.Entries);

        // Same stored instance, not merely an equal one: the duplicate must leave the original row
        // untouched rather than overwrite it with an identical-looking replacement.
        Assert.Same(first.Registered, entry);
        Assert.Same(first.Registered, duplicate.Registered);
        Assert.Equal(first.Registered.FirstSeenUtc, duplicate.Registered.FirstSeenUtc);
    }

    [Then("an audit log entry records the duplicate")]
    public void ThenAnAuditLogEntryRecordsTheDuplicate()
    {
        DocumentIngestResult first = RequireFirstResult();
        DocumentIngestResult duplicate = RequireDuplicateResult();

        Assert.Equal(2, state.AuditLog.Entries.Count);

        IngestAuditEntry registered = state.AuditLog.Entries[0];
        Assert.Equal(IngestAuditEventType.DocumentRegistered, registered.EventType);

        IngestAuditEntry duplicateEntry = state.AuditLog.Entries[1];
        Assert.Equal(IngestAuditEventType.DuplicateSubmissionIgnored, duplicateEntry.EventType);
        Assert.Equal(duplicate.ContentHash, duplicateEntry.ContentHash);
        Assert.Equal(SpecimenFileName, duplicateEntry.Submitted.FileName);

        // The entry has to say which registration the duplicate collided with, or it records that
        // something happened without recording what.
        Assert.Equal(first.Registered.FirstSeenUtc, duplicateEntry.FirstRegisteredUtc);
        Assert.True(
            duplicateEntry.RecordedUtc >= duplicateEntry.FirstRegisteredUtc,
            "The duplicate was recorded as happening before the registration it duplicates.");
    }

    private DocumentIngestService RequireService() =>
        state.Service ?? throw new InvalidOperationException("The Given step did not construct the ingest service.");

    private async Task<DocumentIngestResult> IngestSpecimenAsync()
    {
        DocumentIngestService service = RequireService();
        string specimenPath = state.SpecimenPath
            ?? throw new InvalidOperationException("The Given step did not resolve the specimen path.");

        return await service.IngestAsync(specimenPath);
    }

    private DocumentIngestResult RequireFirstResult() =>
        state.FirstResult ?? throw new InvalidOperationException("No first submission was made by an earlier step.");

    private DocumentIngestResult RequireDuplicateResult() =>
        state.DuplicateResult ?? throw new InvalidOperationException("No duplicate submission was made by an earlier step.");
}
