using Cbix.Core.Diagnostics;
using Cbix.Core.Ingest;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Cbix.Core.Workflows;

/// <summary>
/// The graph's entry node: turns a submission into a registered, prepared document, or stops the run
/// (design 5.1, 5.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>A thin adapter over <see cref="DocumentIngestService"/>, and thin on purpose.</b> Hashing,
/// containment, dedupe, the text layer and document preparation are all in the service, which is
/// testable without a workflow runtime and reusable by the backfill path later. This node's entire
/// job is to be the place in the graph where that happens.
/// </para>
/// <para>
/// <b>Failures are not caught here.</b> The service raises typed refusals -
/// <see cref="IngestRootViolationException"/>, <see cref="DocumentNotIngestibleException"/>,
/// <see cref="Cbix.Core.Documents.DocumentPreparationException"/> - each already logged as a
/// structured event at the point of decision. Swallowing one into a null-shaped message would give
/// the run a document-shaped hole and let triage ask a model about a document that was never
/// prepared. Letting them propagate makes MAF surface an <c>ExecutorFailedEvent</c> and end the run,
/// which is the correct posture until Sprint 03's retry and review edges exist to route them.
/// </para>
/// <para>
/// <b>A duplicate ends the run here, and does so by edge rather than by exception.</b> Design 5.1 is
/// explicit that a re-submission's run stops at the registry. The result is still returned - the
/// registry entry and the audit record are real outcomes - but the edge to triage carries only new
/// registrations, so nothing downstream fires. Doing it with a predicate rather than a throw keeps a
/// routine, expected, audited event out of the failure path: a duplicate is not an error, and a run
/// that reported one as a failure would train operators to ignore failures.
/// </para>
/// </remarks>
public sealed partial class DocumentIngestExecutor : Executor<DocumentSubmission, DocumentIngestResult>
{
    private readonly DocumentIngestService _ingestService;
    private readonly ILogger<DocumentIngestExecutor> _logger;

    /// <summary>Initialises a new <see cref="DocumentIngestExecutor"/>.</summary>
    /// <param name="ingestService">The ingest gate this node runs.</param>
    /// <param name="logger">Sink for this node's structured run events.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public DocumentIngestExecutor(DocumentIngestService ingestService, ILogger<DocumentIngestExecutor> logger)
        : base(CbixWorkflowNodes.Ingest)
    {
        ArgumentNullException.ThrowIfNull(ingestService);
        ArgumentNullException.ThrowIfNull(logger);

        _ingestService = ingestService;
        _logger = logger;
    }

    /// <summary>Ingests one submission.</summary>
    /// <param name="message">The document being offered to the pipeline.</param>
    /// <param name="context">The workflow context for this superstep.</param>
    /// <param name="cancellationToken">Token that cancels the ingest.</param>
    /// <returns>
    /// The ingest outcome. It is sent along the graph's edges; only the edge to triage accepts it,
    /// and only when <see cref="DocumentIngestResult.IsNewRegistration"/> is <see langword="true"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public override async ValueTask<DocumentIngestResult> HandleAsync(
        DocumentSubmission message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        DocumentIngestResult result = await _ingestService
            .IngestAsync(message.DocumentPath, message.MediaType, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsNewRegistration)
        {
            LogDocumentAdmitted(_logger, result.Registered.DocumentId, result.TextLayer?.PageCount ?? 0);
        }
        else
        {
            LogRunStoppedAtRegistry(_logger, result.Registered.DocumentId, result.Registered.FirstSeenUtc);
        }

        return result;
    }

    /// <summary>Structured event for a document that entered the pipeline.</summary>
    /// <remarks>
    /// The document id is the content hash, which is a digest of the bytes and carries no path, no
    /// file name and nothing an outside party chose - so it is the one document identifier that is
    /// always safe to render into a log line.
    /// </remarks>
    [LoggerMessage(
        EventId = CbixEventIds.WorkflowDocumentAdmitted,
        Level = LogLevel.Information,
        Message = "Ingest admitted a document to the pipeline. Document={DocumentId}, textLayerPages={PageCount}.")]
    private static partial void LogDocumentAdmitted(ILogger logger, string documentId, int pageCount);

    /// <summary>Structured event for a run that ended at the registry because the document was already known.</summary>
    /// <remarks>
    /// Information, not Warning: design 5.1 makes this the designed outcome of a re-submission, and
    /// design 9 lists it as a handled case. It is logged at all because "the run produced nothing" and
    /// "the run was not needed" look identical from outside, and an operator chasing a missing
    /// extraction needs the second answer.
    /// </remarks>
    [LoggerMessage(
        EventId = CbixEventIds.WorkflowRunStoppedAtRegistry,
        Level = LogLevel.Information,
        Message = "Ingest stopped the run at the registry: this document is already registered. "
            + "Document={DocumentId}, firstSeen={FirstSeenUtc:O}.")]
    private static partial void LogRunStoppedAtRegistry(
        ILogger logger,
        string documentId,
        DateTimeOffset firstSeenUtc);
}
