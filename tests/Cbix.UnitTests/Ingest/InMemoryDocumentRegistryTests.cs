using System.Text;

using Cbix.Core.Documents;
using Cbix.Core.Ingest;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Covers the in-memory registry against the port's contract: register-or-return-existing is one
/// atomic operation, the stored row is written once, and concurrent submissions of the same bytes
/// produce exactly one registration.
/// </summary>
public sealed class InMemoryDocumentRegistryTests
{
    private static readonly DateTimeOffset FirstSeen = new(2026, 8, 16, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task RegisterAsync_FirstSubmission_StoresTheCandidate()
    {
        InMemoryDocumentRegistry registry = new();
        DocumentRegistryEntry candidate = Entry("first", FirstSeen);

        DocumentRegistration registration = await registry.RegisterAsync(candidate);

        Assert.True(registration.IsNewRegistration);
        Assert.Same(candidate, registration.Entry);
        Assert.Same(candidate, Assert.Single(registry.Entries));
    }

    [Fact]
    public async Task RegisterAsync_SameContentHash_KeepsTheOriginalRow()
    {
        InMemoryDocumentRegistry registry = new();
        DocumentRegistryEntry original = Entry("first", FirstSeen);
        DocumentRegistryEntry resubmission = Entry("first", FirstSeen.AddHours(4));

        await registry.RegisterAsync(original);
        DocumentRegistration duplicate = await registry.RegisterAsync(resubmission);

        Assert.False(duplicate.IsNewRegistration);

        // The pre-existing row comes back, not the candidate just offered: FirstSeenUtc has to keep
        // meaning "when these bytes first entered the pipeline" however many times they arrive.
        Assert.Same(original, duplicate.Entry);
        Assert.Equal(FirstSeen, duplicate.Entry.FirstSeenUtc);
        Assert.Same(original, Assert.Single(registry.Entries));
    }

    [Fact]
    public async Task RegisterAsync_DifferentContent_RegistersSeparately()
    {
        InMemoryDocumentRegistry registry = new();

        await registry.RegisterAsync(Entry("first", FirstSeen));
        DocumentRegistration second = await registry.RegisterAsync(Entry("second", FirstSeen));

        Assert.True(second.IsNewRegistration);
        Assert.Equal(2, registry.Entries.Count);
    }

    [Fact]
    public async Task RegisterAsync_ConcurrentSubmissionsOfTheSameBytes_RegisterExactlyOnce()
    {
        // The port promises atomicity because check-then-act loses this race, and an in-memory
        // stand-in that lost it would let the suite prove a property production does not have.
        InMemoryDocumentRegistry registry = new();
        const int Submissions = 64;

        DocumentRegistration[] registrations = await Task.WhenAll(
            Enumerable.Range(0, Submissions)
                .Select(index => Task.Run(() => registry.RegisterAsync(Entry("contended", FirstSeen.AddSeconds(index))))));

        Assert.Single(registry.Entries);
        Assert.Equal(1, registrations.Count(registration => registration.IsNewRegistration));
        Assert.All(registrations, registration => Assert.Same(registry.Entries.Single(), registration.Entry));
    }

    [Fact]
    public async Task RegisterAsync_NullCandidate_Throws()
    {
        InMemoryDocumentRegistry registry = new();

        await Assert.ThrowsAsync<ArgumentNullException>(() => registry.RegisterAsync(null!));
    }

    [Fact]
    public async Task RegisterAsync_CancelledToken_DoesNotRegister()
    {
        InMemoryDocumentRegistry registry = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.RegisterAsync(Entry("first", FirstSeen), cancellation.Token));

        Assert.Empty(registry.Entries);
    }

    private static DocumentRegistryEntry Entry(string content, DateTimeOffset firstSeen)
    {
        ContentHash hash = ContentHash.ComputeSha256(Encoding.UTF8.GetBytes(content));
        DocumentReference reference = new(
            hash.Canonical,
            new Uri($"file:///ingest/{content}.pdf"),
            $"{content}.pdf");

        return new DocumentRegistryEntry(hash, reference, content.Length, firstSeen);
    }
}
