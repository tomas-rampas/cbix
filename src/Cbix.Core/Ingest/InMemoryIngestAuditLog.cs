using System.Collections.Concurrent;

namespace Cbix.Core.Ingest;

/// <summary>
/// A process-local <see cref="IIngestAuditLog"/> holding audit entries in memory, in arrival order.
/// </summary>
/// <remarks>
/// The PoC and test sink; the durable one arrives with the persistence wiring in Sprint 03. It is
/// append-only like the contract it implements - <see cref="Entries"/> hands out a snapshot, so a
/// reader cannot reach in and edit the trail it is inspecting.
/// </remarks>
public sealed class InMemoryIngestAuditLog : IIngestAuditLog
{
    private readonly ConcurrentQueue<IngestAuditEntry> _entries = new();

    /// <summary>Gets a point-in-time snapshot of the trail, oldest entry first.</summary>
    public IReadOnlyList<IngestAuditEntry> Entries => [.. _entries];

    /// <inheritdoc/>
    public Task RecordAsync(IngestAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        _entries.Enqueue(entry);

        return Task.CompletedTask;
    }
}
