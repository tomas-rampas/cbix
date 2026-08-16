using System.Collections.Concurrent;

namespace Cbix.Core.Ingest;

/// <summary>
/// A process-local <see cref="IDocumentRegistry"/> holding registrations in memory.
/// </summary>
/// <remarks>
/// <para>
/// This is the PoC and test implementation. The SQL Server-backed registry arrives with the
/// persistence wiring in Sprint 03; the DDL it will target is already checked in at
/// <c>db/schema/document_registry.sql</c>, so the two agree on shape before either has to change.
/// </para>
/// <para>
/// <b>It keeps the port's atomicity guarantee for real.</b> The whole point of
/// <see cref="IDocumentRegistry.RegisterAsync"/> being one operation is that check-then-act loses
/// races; an in-memory stand-in that did a <c>ContainsKey</c> followed by an <c>Add</c> would
/// reintroduce exactly the window the port was shaped to remove, and would let a test suite prove a
/// property the production implementation does not have.
/// </para>
/// <para>
/// Registrations live for the lifetime of the instance and are never evicted. That is correct for a
/// test or a single-document PoC run and wrong for a long-lived process, which is the other reason
/// this type is not the production registry.
/// </para>
/// </remarks>
public sealed class InMemoryDocumentRegistry : IDocumentRegistry
{
    private readonly ConcurrentDictionary<string, DocumentRegistryEntry> _entries =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Gets a point-in-time snapshot of every registered entry, for tests and diagnostics. The
    /// order is unspecified.
    /// </summary>
    public IReadOnlyCollection<DocumentRegistryEntry> Entries => [.. _entries.Values];

    /// <inheritdoc/>
    public Task<DocumentRegistration> RegisterAsync(
        DocumentRegistryEntry candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        // GetOrAdd with a value (not a factory) is a single atomic operation, and reference equality
        // - not the record's value equality - is what distinguishes "this call stored it" from "this
        // call found an equal row somebody else stored".
        DocumentRegistryEntry stored = _entries.GetOrAdd(candidate.DocumentId, candidate);

        return Task.FromResult(new DocumentRegistration(stored, ReferenceEquals(stored, candidate)));
    }
}
