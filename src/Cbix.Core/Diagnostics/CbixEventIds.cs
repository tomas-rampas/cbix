namespace Cbix.Core.Diagnostics;

/// <summary>
/// Every structured-logging event id CBIX emits, in one place.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists because the alternative was measured failing.</b> Ids were previously written
/// as literals at each <c>LoggerMessage</c> site, and <c>1015</c> ended up on two unrelated events -
/// <see cref="IngestPreparationCredentialFailure"/> (Critical: the provider credential is refused, so
/// every document in the estate will fail) and <see cref="IngestTextLayerOpenFailed"/> (Error: one
/// document could not be opened). Nothing caught it: both compiled, both logged, and an alert rule
/// keyed on 1015 would have fired on routine per-document noise while a fleet-wide outage hid inside
/// the same bucket. An event id is an API - dashboards and alert rules are written against it - and
/// scattered literals give that API no namespace and no uniqueness check.
/// </para>
/// <para>
/// <b>Adding an event.</b> Add a constant here, in the block for its area, and use it at the
/// <c>LoggerMessage</c> site. <c>LoggerMessageAttribute.EventId</c> takes a compile-time constant, so
/// these are <c>const int</c> and reference cleanly from the attribute.
/// <c>CbixEventIdTests</c> reflects over every <c>LoggerMessage</c> in the assembly and fails on a
/// duplicate, so the collision above cannot recur silently - which is the actual control. This class
/// is what makes it easy to comply with; the test is what makes it true.
/// </para>
/// <para>
/// <b>Blocks are allocated by area, with gaps.</b> Contiguity is not a goal - room to grow inside a
/// block is, because that is what stops the next event being squeezed into a neighbouring area's
/// range and losing the one property a range gives an operator: that ids near each other come from
/// near each other.
/// </para>
/// </remarks>
public static class CbixEventIds
{
    // ---------------------------------------------------------------------------------------------
    // 1010-1019: ingest - the document trust boundary and the local text layer.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A submission was refused at the containment boundary (design 5.1, Sprint 01 addendum).</summary>
    public const int IngestContainmentRefusal = 1010;

    /// <summary>A submission inside the root is not a usable document.</summary>
    public const int IngestDocumentRefused = 1011;

    /// <summary>An open or read failed for a reason ingest did not decide.</summary>
    public const int IngestDocumentReadFailed = 1012;

    /// <summary>A document's text layer could not be produced.</summary>
    public const int IngestTextLayerRefused = 1013;

    /// <summary>A submission's leaf name or media type can never construct a document reference.</summary>
    public const int IngestUnacceptableReferenceShape = 1014;

    /// <summary>
    /// Document preparation was refused by the provider's credential - a fleet-wide outage, not a
    /// document problem.
    /// </summary>
    public const int IngestPreparationCredentialFailure = 1015;

    /// <summary>Document preparation failed for a reason specific to this document or this moment.</summary>
    public const int IngestPreparationFailed = 1016;

    /// <summary>
    /// A document could not be opened for text-layer extraction.
    /// </summary>
    /// <remarks>
    /// <b>Renumbered from 1015 to 1019</b> when this registry landed; it was the losing half of the
    /// collision described on this class. Anything keyed on 1015 for "could not open for extraction"
    /// - and there should be nothing, since no dashboard has been built yet - moves here.
    /// </remarks>
    public const int IngestTextLayerOpenFailed = 1019;

    // ---------------------------------------------------------------------------------------------
    // 1017-1018: page rasterisation. Inside the ingest block by history rather than by design; left
    // where they are because moving a live id costs more than the tidiness is worth.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Page rasterisation refused a document against its render budget.</summary>
    public const int PageRenderRefused = 1017;

    /// <summary>The page rasteriser faulted inside native code on untrusted input.</summary>
    public const int PageRenderFaulted = 1018;

    // ---------------------------------------------------------------------------------------------
    // 1020-1039: workflow executors (design 5.2).
    // ---------------------------------------------------------------------------------------------

    /// <summary>A document entered the pipeline.</summary>
    public const int WorkflowDocumentAdmitted = 1020;

    /// <summary>A run ended at the registry because the document was already known.</summary>
    public const int WorkflowRunStoppedAtRegistry = 1021;

    /// <summary>Triage is about to make the run's first - and cache-priming - model call.</summary>
    public const int WorkflowTriageStarting = 1022;

    /// <summary>A run reached the persist step.</summary>
    public const int WorkflowRunCompleted = 1023;

    // ---------------------------------------------------------------------------------------------
    // 1040-1059: host composition and startup.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Startup record of the endpoint every model call in this run will go to.</summary>
    public const int HostProviderEndpointResolved = 1040;

    /// <summary>Startup record of how documents in this run will be presented to the model.</summary>
    public const int HostDocumentPresentationResolved = 1041;
}
