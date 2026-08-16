using Cbix.Core.Ingest;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/IngestTextLayerExtraction.feature</c> (story S01-11).
/// <para>
/// A dumb typed carrier, as elsewhere: the steps own the behaviour, this owns only what has to
/// survive between them. The registry and audit log are the in-memory implementations, created per
/// scenario, so "already registered" is a fact about this scenario rather than about whatever ran
/// before it.
/// </para>
/// </summary>
public sealed class IngestTextLayerExtractionState
{
    /// <summary>Gets the registry the scenario's ingest service writes to.</summary>
    public InMemoryDocumentRegistry Registry { get; } = new();

    /// <summary>Gets the audit trail the scenario's ingest service appends to.</summary>
    public InMemoryIngestAuditLog AuditLog { get; } = new();

    /// <summary>Gets or sets the counting decorator wrapped around the real PDFPig extractor.</summary>
    /// <remarks>
    /// The extraction itself is the production one, run against the real specimen: the scenario is
    /// about what PDFPig actually produces from that document, so stubbing it would prove nothing.
    /// The decorator only counts, because "no second text layer is extracted" is a statement about
    /// how often the collaborator was called and there is no other way to observe that.
    /// </remarks>
    public CountingTextLayerExtractor? Extractor { get; set; }

    /// <summary>Gets or sets the ingest service under test.</summary>
    public DocumentIngestService? Service { get; set; }

    /// <summary>Gets or sets the absolute path of the specimen being submitted.</summary>
    public string? SpecimenPath { get; set; }

    /// <summary>Gets or sets the outcome of the first submission.</summary>
    public DocumentIngestResult? FirstResult { get; set; }

    /// <summary>Gets or sets the outcome of the second, duplicate submission.</summary>
    public DocumentIngestResult? DuplicateResult { get; set; }
}
