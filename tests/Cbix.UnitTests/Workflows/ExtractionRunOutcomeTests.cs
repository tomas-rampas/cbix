using Cbix.Core.Documents;
using Cbix.Core.Ingest;
using Cbix.Core.Workflows;

namespace Cbix.UnitTests.Workflows;

/// <summary>
/// Tests for the workflow's terminal value and the node that produces it on the duplicate branch
/// (story S01-13).
/// </summary>
/// <remarks>
/// The invariant under test throughout is that <b>a run's ending is stated, never inferred</b>. Both
/// branches of the graph yield an outcome carrying its disposition, so nothing downstream has to read
/// silence - which is also what a crash produces - as a result.
/// </remarks>
public sealed class ExtractionRunOutcomeTests
{
    [Fact]
    public void Constructor_RefusesADuplicateThatClaimsExtractedSections()
    {
        // The combination is incoherent by construction: a duplicate's run stops at the registry, so
        // no section agent ran and there is nothing to count. A non-zero count there would mean the
        // short-circuit had failed silently - and the failure it would be reporting is precisely the
        // expensive one, because reaching a section agent means having paid for it.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new ExtractionRunOutcome(Reference(), ExtractionRunDisposition.DuplicateIgnored, sectionCount: 3));

        Assert.Equal("sectionCount", error.ParamName);
    }

    [Fact]
    public void Constructor_AllowsAPersistedRunWithNoSections()
    {
        // The mirror case must stay legal: the Sprint 01 section slot is a stub that extracts nothing,
        // so zero is the normal count today. Only the duplicate combination is contradictory.
        ExtractionRunOutcome outcome = new(Reference(), ExtractionRunDisposition.Persisted, sectionCount: 0);

        Assert.Equal(ExtractionRunDisposition.Persisted, outcome.Disposition);
        Assert.Equal(0, outcome.SectionCount);
    }

    [Fact]
    public void Constructor_RejectsANegativeSectionCount() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExtractionRunOutcome(Reference(), ExtractionRunDisposition.Persisted, sectionCount: -1));

    [Fact]
    public void Constructor_RejectsAMissingDocument() =>
        Assert.Throws<ArgumentNullException>(
            () => new ExtractionRunOutcome(null!, ExtractionRunDisposition.Persisted, sectionCount: 0));

    [Fact]
    public async Task DuplicateTerminal_ReportsTheDuplicateIgnoredDisposition()
    {
        DocumentIngestResult duplicate = IngestResult(isNewRegistration: false);

        ExtractionRunOutcome outcome = await new DuplicateTerminalExecutor()
            .HandleAsync(duplicate, context: null!);

        Assert.Equal(ExtractionRunDisposition.DuplicateIgnored, outcome.Disposition);
        Assert.Equal(0, outcome.SectionCount);
        Assert.Same(duplicate.Submitted, outcome.Document);
    }

    [Fact]
    public async Task DuplicateTerminal_RefusesANewRegistration()
    {
        // The backstop behind the edge condition. If the graph were ever mis-wired so that new
        // documents took the duplicate edge, every one of them would be reported as already processed
        // and never extracted - a silent, total loss of throughput that looks like a healthy pipeline
        // reporting completed runs. Refusing loudly is the only outcome worth having.
        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            async () => await new DuplicateTerminalExecutor()
                .HandleAsync(IngestResult(isNewRegistration: true), context: null!));

        Assert.Equal("message", error.ParamName);
        Assert.Contains("never extracted", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A document reference with a well-formed synthetic identity.</summary>
    private static DocumentReference Reference() =>
        new(
            ContentHash.ComputeSha256("outcome-test"u8.ToArray()).Canonical,
            new Uri(Path.Combine(Path.GetTempPath(), "outcome-test.pdf")),
            "outcome-test.pdf");

    /// <summary>An ingest result on either side of the registry decision.</summary>
    /// <remarks>
    /// Built directly rather than through the ingest service: these tests are about what the terminal
    /// does with the two shapes, and driving a real registration to produce them would make them
    /// depend on the whole containment and hashing path for no added coverage.
    /// </remarks>
    private static DocumentIngestResult IngestResult(bool isNewRegistration)
    {
        DocumentReference submitted = Reference();
        ContentHash hash = ContentHash.ComputeSha256("outcome-test"u8.ToArray());
        DocumentRegistryEntry entry = new(hash, submitted, byteLength: 12, DateTimeOffset.UnixEpoch);

        return new DocumentIngestResult(
            submitted,
            entry,
            isNewRegistration,
            isNewRegistration ? new TextLayer(submitted.DocumentId, ["page one"]) : null,
            contentHandle: null);
    }
}
