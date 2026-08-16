using Cbix.Core.Ingest;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/IngestContentHashAndDedupe.feature</c> (story S01-10).
/// <para>
/// A dumb typed carrier: the steps own the behaviour, this owns only what has to survive from one
/// step to the next. The registry and the audit log are the in-memory implementations, created per
/// scenario, so "has not been submitted before" is a fact about this scenario rather than about
/// whatever ran before it.
/// </para>
/// </summary>
public sealed class IngestContentHashAndDedupeState
{
    /// <summary>Gets the registry the scenario's ingest service writes to.</summary>
    public InMemoryDocumentRegistry Registry { get; } = new();

    /// <summary>Gets the audit trail the scenario's ingest service appends to.</summary>
    public InMemoryIngestAuditLog AuditLog { get; } = new();

    /// <summary>Gets or sets the ingest service under test.</summary>
    public DocumentIngestService? Service { get; set; }

    /// <summary>Gets or sets the absolute path of the specimen being submitted.</summary>
    public string? SpecimenPath { get; set; }

    /// <summary>Gets or sets the outcome of the first submission.</summary>
    public DocumentIngestResult? FirstResult { get; set; }

    /// <summary>Gets or sets the outcome of the second, duplicate submission.</summary>
    public DocumentIngestResult? DuplicateResult { get; set; }

    /// <summary>Gets or sets the refusal raised by a submission that escaped the ingest root.</summary>
    public IngestRootViolationException? Refusal { get; set; }
}
