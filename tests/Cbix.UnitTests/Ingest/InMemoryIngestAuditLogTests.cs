using System.Text;

using Cbix.Core.Documents;
using Cbix.Core.Ingest;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Covers the in-memory audit sink: entries accumulate in arrival order, repeat submissions are
/// never collapsed, and a reader cannot edit the trail it is inspecting.
/// </summary>
public sealed class InMemoryIngestAuditLogTests
{
    private static readonly DateTimeOffset Recorded = new(2026, 8, 16, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordAsync_KeepsArrivalOrder()
    {
        InMemoryIngestAuditLog log = new();

        await log.RecordAsync(AuditEntry(IngestAuditEventType.DocumentRegistered, Recorded));
        await log.RecordAsync(AuditEntry(IngestAuditEventType.DuplicateSubmissionIgnored, Recorded.AddMinutes(5)));

        Assert.Equal(
            [IngestAuditEventType.DocumentRegistered, IngestAuditEventType.DuplicateSubmissionIgnored],
            log.Entries.Select(entry => entry.EventType));
    }

    [Fact]
    public async Task RecordAsync_RepeatedDuplicates_AreNeverCollapsed()
    {
        // The repetition is the signal: three submissions of one document is an upstream feed
        // looping, and a log that deduplicated its own entries would hide exactly that.
        InMemoryIngestAuditLog log = new();

        for (int minute = 0; minute < 3; minute++)
        {
            await log.RecordAsync(AuditEntry(IngestAuditEventType.DuplicateSubmissionIgnored, Recorded.AddMinutes(minute)));
        }

        Assert.Equal(3, log.Entries.Count);
    }

    [Fact]
    public async Task Entries_IsASnapshot()
    {
        InMemoryIngestAuditLog log = new();
        await log.RecordAsync(AuditEntry(IngestAuditEventType.DocumentRegistered, Recorded));

        IReadOnlyList<IngestAuditEntry> snapshot = log.Entries;
        await log.RecordAsync(AuditEntry(IngestAuditEventType.DuplicateSubmissionIgnored, Recorded.AddMinutes(1)));

        Assert.Single(snapshot);
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public async Task RecordAsync_ConcurrentAppends_LoseNothing()
    {
        InMemoryIngestAuditLog log = new();
        const int Appends = 64;

        await Task.WhenAll(
            Enumerable.Range(0, Appends)
                .Select(index => Task.Run(() =>
                    log.RecordAsync(AuditEntry(IngestAuditEventType.DuplicateSubmissionIgnored, Recorded.AddSeconds(index))))));

        Assert.Equal(Appends, log.Entries.Count);
    }

    [Fact]
    public async Task RecordAsync_NullEntry_Throws()
    {
        InMemoryIngestAuditLog log = new();

        await Assert.ThrowsAsync<ArgumentNullException>(() => log.RecordAsync(null!));
    }

    [Fact]
    public async Task RecordAsync_CancelledToken_RecordsNothing()
    {
        InMemoryIngestAuditLog log = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => log.RecordAsync(AuditEntry(IngestAuditEventType.DocumentRegistered, Recorded), cancellation.Token));

        Assert.Empty(log.Entries);
    }

    private static IngestAuditEntry AuditEntry(IngestAuditEventType eventType, DateTimeOffset recordedUtc)
    {
        ContentHash hash = ContentHash.ComputeSha256(Encoding.UTF8.GetBytes("DE specimen"));
        DocumentReference reference = new(hash.Canonical, new Uri("file:///ingest/DE_SPECIMEN.pdf"), "DE_SPECIMEN.pdf");

        return new IngestAuditEntry(eventType, hash, reference, recordedUtc, Recorded);
    }
}
