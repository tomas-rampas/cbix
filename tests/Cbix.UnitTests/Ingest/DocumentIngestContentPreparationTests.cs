using System.Text;

using Cbix.Core.Diagnostics;
using Cbix.Core.Documents;
using Cbix.Core.Ingest;
using Cbix.Providers.Anthropic;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cbix.UnitTests.Ingest;

/// <summary>
/// Covers the ingest gate's use of the document-content port (story S01-12): who prepares a
/// document, how often, and what happens to the run when preparation fails.
/// </summary>
/// <remarks>
/// <para>
/// The provider here is a stub rather than a profile, and deliberately so: the properties under test
/// are <em>how often</em> and <em>with what</em> ingest asks, which only a seam can answer. That the
/// Claude profile really uploads once through this path is asserted at the wire by the S01-12 BDD
/// scenarios; these tests are about the gate's own logic, which must hold for whichever profile is
/// configured.
/// </para>
/// <para>
/// The port is the type ingest depends on, so a stub implementing it is not a weaker test than a
/// real profile - it is the honest one. If ingest could tell the difference, the port would have
/// failed.
/// </para>
/// </remarks>
public sealed class DocumentIngestContentPreparationTests : IDisposable
{
    private const long MaxDocumentBytes = 4096;

    private readonly DirectoryInfo _workspace =
        Directory.CreateTempSubdirectory("cbix-ingest-preparation-");

    private readonly InMemoryDocumentRegistry _registry = new();
    private readonly InMemoryIngestAuditLog _auditLog = new();

    public void Dispose()
    {
        try
        {
            _workspace.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a suite over.
        }
    }

    [Fact]
    public async Task Ingest_PreparesANewlyRegisteredDocumentExactlyOnce()
    {
        RecordingContentProvider provider = new();
        DocumentIngestService service = CreateService(provider);

        DocumentIngestResult result = await service.IngestAsync(WriteDocument("DE_SPECIMEN.pdf"));

        Assert.Equal(1, provider.CallCount);

        // The handle is the run's document handle: what triage and the section agents carry instead
        // of the document, and what a checkpoint persists.
        Assert.NotNull(result.ContentHandle);
        Assert.Equal(result.Registered.DocumentId, result.ContentHandle!.DocumentId);
        Assert.Equal(RecordingContentProvider.Token, result.ContentHandle.ProviderToken);

        // Still the whole ingest result, not a replacement for it: the grounding corpus rides along
        // as before.
        Assert.NotNull(result.TextLayer);
    }

    [Fact]
    public async Task Ingest_AsksTheProviderForTheDocumentItRegistered()
    {
        // Mis-addressing this call is how one document's content gets prepared under another's
        // identity, which is cache poisoning and mis-attributed provenance at once.
        RecordingContentProvider provider = new();
        DocumentIngestService service = CreateService(provider);

        DocumentIngestResult result = await service.IngestAsync(WriteDocument("DE_SPECIMEN.pdf"));

        DocumentReference asked = Assert.Single(provider.Calls);
        Assert.Equal(result.Registered.DocumentId, asked.DocumentId);
        Assert.Equal(result.Submitted.Location, asked.Location);
    }

    [Fact]
    public async Task Ingest_DoesNotEvenAskTheProviderForADuplicate()
    {
        // Not "the memo makes it cheap" - ingest does not ask at all, so the short-circuit is a
        // property of the gate rather than of whichever profile happens to be configured. A profile
        // whose preparation is a page render rather than an upload gets the same guarantee.
        RecordingContentProvider provider = new();
        DocumentIngestService service = CreateService(provider);
        string path = WriteDocument("DE_SPECIMEN.pdf");

        await service.IngestAsync(path);
        DocumentIngestResult duplicate = await service.IngestAsync(path);

        Assert.Equal(1, provider.CallCount);
        Assert.False(duplicate.IsNewRegistration);
        Assert.Null(duplicate.ContentHandle);
        Assert.Null(duplicate.TextLayer);
    }

    [Fact]
    public async Task Ingest_WithoutAProviderRegistersAndExtractsButProducesNoHandle()
    {
        // A null provider is a configuration, not a degraded mode: ingest is meaningful without a
        // model, and S01-13 decides whether a profile is active at all.
        DocumentIngestService service = CreateService(contentProvider: null);

        DocumentIngestResult result = await service.IngestAsync(WriteDocument("DE_SPECIMEN.pdf"));

        Assert.True(result.IsNewRegistration);
        Assert.NotNull(result.TextLayer);
        Assert.Null(result.ContentHandle);
    }

    [Fact]
    public async Task Ingest_PreparesOnlyAfterTheTextLayerSucceeds()
    {
        // Ordering with a cost attached: the text layer is local and free, preparation is a network
        // call against a paid API. A document that cannot be read at all must never reach the
        // provider.
        RecordingContentProvider provider = new();
        DocumentIngestService service = CreateService(provider, new UnreadableExtractor());

        await Assert.ThrowsAsync<DocumentNotIngestibleException>(
            () => service.IngestAsync(WriteDocument("DE_SPECIMEN.pdf")));

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task Ingest_PropagatesATransientPreparationFailureWithItsTransienceIntact()
    {
        // The retry layer's food. Swallowing this into a handle-less "success" would drop a document
        // that a moment later would have uploaded fine.
        FailingContentProvider provider = new(isTransient: true);
        DocumentIngestService service = CreateService(provider);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => service.IngestAsync(WriteDocument("DE_SPECIMEN.pdf")));

        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task Ingest_PropagatesAPermanentPreparationFailureAndLeavesTheTrailIntact()
    {
        // The cost of the ordering, asserted rather than described: registration and its audit entry
        // survive a preparation failure, so a re-submission is a duplicate and never re-prepares.
        // Recovering that document is Sprint 03's extraction_runs and review queue, exactly as it is
        // for a failed extraction.
        FailingContentProvider provider = new(isTransient: false);
        DocumentIngestService service = CreateService(provider);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => service.IngestAsync(WriteDocument("DE_SPECIMEN.pdf")));

        Assert.False(error.IsTransient);
        Assert.Single(_registry.Entries);
        Assert.Single(_auditLog.Entries);
    }

    [Fact]
    public async Task Ingest_ReportsACredentialFailureAsItsOwnEvent()
    {
        // The signal that separates "this document is bad" from "the fleet is down". A revoked key
        // fails every document permanently, and without its own event that arrives as N
        // indistinguishable review entries with nothing for an alert rule to fire on.
        //
        // The failure kind is written here through the ADAPTER's own constant and read by the
        // service through its private mirror of the same string. That is deliberate: the two
        // spellings cannot be shared across the containment boundary, so this test is what holds
        // them together - rename either side and the event below stops being emitted.
        CapturingLogger logger = new();
        FailingContentProvider provider = new(isTransient: false, failureKind: ClaudeDocumentContentProvider.CredentialFailure);
        DocumentIngestService service = CreateService(provider, logger: logger);

        await Assert.ThrowsAsync<DocumentPreparationException>(
            () => service.IngestAsync(WriteDocument("DE_SPECIMEN.pdf")));

        LoggedEvent credential = Assert.Single(logger.Entries, entry => entry.EventId == CbixEventIds.IngestPreparationCredentialFailure);
        Assert.Equal(LogLevel.Critical, credential.Level);
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId == CbixEventIds.IngestPreparationFailed);
    }

    [Fact]
    public async Task Ingest_ReportsADocumentSpecificFailureAsTheOrdinaryEvent()
    {
        // The other side of the split: a failure that is a property of this document must NOT look
        // like a fleet outage, or the alert that matters gets tuned out.
        CapturingLogger logger = new();
        FailingContentProvider provider = new(isTransient: true, failureKind: "transport");
        DocumentIngestService service = CreateService(provider, logger: logger);

        await Assert.ThrowsAsync<DocumentPreparationException>(
            () => service.IngestAsync(WriteDocument("DE_SPECIMEN.pdf")));

        LoggedEvent failure = Assert.Single(logger.Entries, entry => entry.EventId == CbixEventIds.IngestPreparationFailed);
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.DoesNotContain(logger.Entries, entry => entry.EventId == CbixEventIds.IngestPreparationCredentialFailure);
    }

    [Fact]
    public async Task Ingest_RefusesAHandleThatNamesAnotherDocument()
    {
        // A provider that answered with another document's handle would have every extracted value
        // recorded against these bytes while the model read different ones. The result type refuses
        // to be constructed that way, so the run fails instead of publishing a lie.
        DocumentIngestService service = CreateService(new MisaddressedContentProvider());

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => service.IngestAsync(WriteDocument("DE_SPECIMEN.pdf")));

        Assert.Contains("sha256:some-other-document", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Result_RefusesAHandleOnADuplicate()
    {
        // The direction of the invariant that CAN be checked. The converse - a new registration
        // always carries a handle - is not true and must not be asserted: whether any provider is
        // configured is a composition decision this type cannot see.
        // The registry is the sole authority for identity and derives it from the bytes, so a
        // reference it would accept has to spell its id as the canonical hash.
        ContentHash hash = new(ContentHash.Sha256Algorithm, new string('a', 64));
        DocumentReference submitted = new(hash.Canonical, new Uri(Path.Combine(_workspace.FullName, "a.pdf")), "a.pdf");
        DocumentRegistryEntry registered = new(hash, submitted, byteLength: 24, DateTimeOffset.UnixEpoch);

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new DocumentIngestResult(
                submitted,
                registered,
                isNewRegistration: false,
                textLayer: null,
                contentHandle: new DocumentContentHandle(hash.Canonical, "claude-native-pdf", "file_x")));

        Assert.Equal("contentHandle", error.ParamName);
    }

    /// <summary>Writes a document into the ingest root and returns its path.</summary>
    private string WriteDocument(string fileName, string content = "cross-border instruction")
    {
        string path = Path.Combine(_workspace.FullName, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);

        return path;
    }

    private DocumentIngestService CreateService(
        IDocumentContentProvider? contentProvider,
        ITextLayerExtractor? extractor = null,
        ILogger<DocumentIngestService>? logger = null) =>
        new(
            _registry,
            _auditLog,
            new DocumentIngestOptions(_workspace.FullName, MaxDocumentBytes),
            extractor ?? new FixedTextLayerExtractor(),
            logger ?? NullLogger<DocumentIngestService>.Instance,
            timeProvider: null,
            documentContentProvider: contentProvider);

    /// <summary>One structured event, reduced to the parts an alert rule keys on.</summary>
    private sealed record LoggedEvent(LogLevel Level, int EventId);

    /// <summary>
    /// Captures log entries so the preparation-failure split can be asserted rather than assumed.
    /// </summary>
    /// <remarks>
    /// The event id is the assertion target, not the message text. An alert rule keys on the id, so
    /// the id is the log's actual contract; wording is prose that may legitimately be reworded.
    /// </remarks>
    private sealed class CapturingLogger : ILogger<DocumentIngestService>
    {
        private readonly List<LoggedEvent> _entries = [];

        public IReadOnlyList<LoggedEvent> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _entries.Add(new LoggedEvent(logLevel, eventId.Id));
    }

    /// <summary>An extractor that answers with a fixed page, so these tests are about preparation only.</summary>
    private sealed class FixedTextLayerExtractor : ITextLayerExtractor
    {
        public Task<TextLayer> ExtractAsync(DocumentReference document, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);

            return Task.FromResult(new TextLayer(document.DocumentId, ["extracted page one"]));
        }
    }

    /// <summary>An extractor standing in for a file the PDF parser cannot read.</summary>
    private sealed class UnreadableExtractor : ITextLayerExtractor
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

    /// <summary>A profile stand-in that records what it was asked to prepare.</summary>
    private sealed class RecordingContentProvider : IDocumentContentProvider
    {
        internal const string Token = "file_stub_prepared";

        private readonly List<DocumentReference> _calls = [];

        internal IReadOnlyList<DocumentReference> Calls => _calls;

        internal int CallCount => _calls.Count;

        public Task<DocumentContent> PrepareAsync(
            DocumentReference document,
            DocumentContentHandle? resumeFrom = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(document);

            _calls.Add(document);

            return Task.FromResult(new DocumentContent(
                [new AIContent()],
                new DocumentContentCapabilities("stub-profile", includesVisualContent: true, isDegraded: false),
                new DocumentContentHandle(document.DocumentId, "stub-profile", Token)));
        }
    }

    /// <summary>A profile stand-in whose preparation fails the way a provider outage does.</summary>
    /// <remarks>
    /// The failure kind is written the way a real profile writes it - into
    /// <see cref="Exception.Data"/> under the adapter's own key - so the service is read through the
    /// same convention production uses rather than through a shape invented for the test.
    /// </remarks>
    private sealed class FailingContentProvider(bool isTransient, string? failureKind = null) : IDocumentContentProvider
    {
        public Task<DocumentContent> PrepareAsync(
            DocumentReference document,
            DocumentContentHandle? resumeFrom = null,
            CancellationToken cancellationToken = default)
        {
            DocumentPreparationException failure =
                new("The stub profile refused to prepare the document.", isTransient);

            if (failureKind is not null)
            {
                failure.Data[ClaudeDocumentContentProvider.FailureKindKey] = failureKind;
            }

            throw failure;
        }
    }

    /// <summary>A profile stand-in that answers with another document's handle.</summary>
    private sealed class MisaddressedContentProvider : IDocumentContentProvider
    {
        public Task<DocumentContent> PrepareAsync(
            DocumentReference document,
            DocumentContentHandle? resumeFrom = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DocumentContent(
                [new AIContent()],
                new DocumentContentCapabilities("stub-profile", includesVisualContent: true, isDegraded: false),
                new DocumentContentHandle("sha256:some-other-document", "stub-profile", "file_wrong")));
    }
}
