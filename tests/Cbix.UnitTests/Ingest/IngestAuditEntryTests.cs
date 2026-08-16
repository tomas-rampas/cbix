using System.Text;

using Cbix.Core.Documents;
using Cbix.Core.Ingest;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Covers the audit entry's construction rules. An audit trail is evidence, so an entry that cannot
/// be true - an undefined outcome, a local-time instant, a registration dated after the submission
/// that observed it - is refused at construction rather than written and explained later.
/// </summary>
public sealed class IngestAuditEntryTests
{
    private static readonly ContentHash SpecimenHash = ContentHash.ComputeSha256(Encoding.UTF8.GetBytes("DE specimen"));

    private static readonly DateTimeOffset Recorded = new(2026, 8, 16, 11, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset FirstRegistered = new(2026, 8, 16, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_DuplicateEvent_KeepsBothInstants()
    {
        IngestAuditEntry entry = new(
            IngestAuditEventType.DuplicateSubmissionIgnored,
            SpecimenHash,
            Reference(SpecimenHash.Canonical),
            Recorded,
            FirstRegistered);

        Assert.Equal(IngestAuditEventType.DuplicateSubmissionIgnored, entry.EventType);
        Assert.Equal(Recorded, entry.RecordedUtc);
        Assert.Equal(FirstRegistered, entry.FirstRegisteredUtc);
    }

    [Fact]
    public void Constructor_UndefinedEventType_Throws()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new IngestAuditEntry(
                (IngestAuditEventType)99,
                SpecimenHash,
                Reference(SpecimenHash.Canonical),
                Recorded,
                FirstRegistered));

        Assert.Equal("eventType", error.ParamName);
    }

    [Fact]
    public void Constructor_SubmittedIdentityNotDerivedFromTheHash_Throws()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new IngestAuditEntry(
                IngestAuditEventType.DocumentRegistered,
                SpecimenHash,
                Reference("sha256:deadbeef"),
                Recorded,
                FirstRegistered));

        Assert.Equal("submitted", error.ParamName);
    }

    [Fact]
    public void Constructor_RegistrationLaterThanTheSubmissionThatObservedIt_Throws()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new IngestAuditEntry(
                IngestAuditEventType.DuplicateSubmissionIgnored,
                SpecimenHash,
                Reference(SpecimenHash.Canonical),
                FirstRegistered,
                Recorded));

        Assert.Equal("firstRegisteredUtc", error.ParamName);
    }

    [Fact]
    public void Constructor_NonUtcTimestamps_Throw()
    {
        DateTimeOffset local = new(2026, 8, 16, 11, 0, 0, TimeSpan.FromHours(2));

        ArgumentException recorded = Assert.Throws<ArgumentException>(
            () => new IngestAuditEntry(
                IngestAuditEventType.DocumentRegistered,
                SpecimenHash,
                Reference(SpecimenHash.Canonical),
                local,
                FirstRegistered));
        Assert.Equal("recordedUtc", recorded.ParamName);

        ArgumentException firstRegistered = Assert.Throws<ArgumentException>(
            () => new IngestAuditEntry(
                IngestAuditEventType.DocumentRegistered,
                SpecimenHash,
                Reference(SpecimenHash.Canonical),
                Recorded,
                local));
        Assert.Equal("firstRegisteredUtc", firstRegistered.ParamName);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => new IngestAuditEntry(
                IngestAuditEventType.DocumentRegistered,
                null!,
                Reference(SpecimenHash.Canonical),
                Recorded,
                FirstRegistered));

        Assert.Throws<ArgumentNullException>(
            () => new IngestAuditEntry(
                IngestAuditEventType.DocumentRegistered,
                SpecimenHash,
                null!,
                Recorded,
                FirstRegistered));
    }

    private static DocumentReference Reference(string documentId) =>
        new(documentId, new Uri("file:///ingest/DE_SPECIMEN.pdf"), "DE_SPECIMEN.pdf");
}
