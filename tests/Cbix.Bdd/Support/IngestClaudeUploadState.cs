using Cbix.Core.Documents;
using Cbix.Core.Ingest;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/IngestClaudeDocumentUpload.feature</c> (story S01-12).
/// </summary>
/// <remarks>
/// <para>
/// Two transports, because the scenarios watch two different conversations. The Files API upload is
/// counted on the transport the ingest service's profile writes to; the agent request is captured on
/// the transport the agent writes to. Sharing one would make "the upload happened once" and "the
/// message carried the block" assertions read each other's traffic.
/// </para>
/// <para>
/// Nothing here is typed as an adapter class - the factory and the profile are reached by reflection
/// and held as the neutral abstractions, so the red phase is a failing assertion rather than a build
/// break, and so this project never names an Anthropic type.
/// </para>
/// </remarks>
public sealed class IngestClaudeUploadState : IDisposable
{
    private bool _disposed;

    /// <summary>Gets the registry the scenario's ingest service writes to.</summary>
    public InMemoryDocumentRegistry Registry { get; } = new();

    /// <summary>Gets the audit trail the scenario's ingest service appends to.</summary>
    public InMemoryIngestAuditLog AuditLog { get; } = new();

    /// <summary>Gets or sets the absolute path of the specimen being submitted.</summary>
    public string? SpecimenPath { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this scenario talks to the real Anthropic API.
    /// </summary>
    /// <remarks>
    /// Set by the <c>@requiresApi</c> arrangement only. It changes what the transports terminate in -
    /// the real network stack rather than canned handlers - and therefore what a Then step may
    /// assert: a forwarded request's body cannot be read without consuming it.
    /// </remarks>
    public bool IsLive { get; set; }

    /// <summary>
    /// Gets or sets the exception an agent run produced, if any.
    /// </summary>
    /// <remarks>
    /// Recorded rather than swallowed because on the live lane the outcome of the call <em>is</em>
    /// the assertion - a refusal there means the beta pairing this story sends is wrong.
    /// </remarks>
    public Exception? AgentRunFailure { get; set; }

    /// <summary>Gets or sets the ingest service under test, wired with the Claude profile.</summary>
    public DocumentIngestService? Service { get; set; }

    /// <summary>Gets or sets the handler counting Files API uploads triggered by ingest.</summary>
    public UploadCountingHandler? UploadCounter { get; set; }

    /// <summary>Gets or sets the canned Files API answering those uploads.</summary>
    public CannedFilesApiHandler? CannedFilesApi { get; set; }

    /// <summary>Gets or sets the transport the profile's client writes to.</summary>
    public HttpClient? UploadTransport { get; set; }

    /// <summary>Gets or sets the handler capturing the outbound agent message request.</summary>
    public CapturingMessagesHandler? MessagesApi { get; set; }

    /// <summary>Gets or sets the transport the agent writes to.</summary>
    public HttpClient? MessagesTransport { get; set; }

    /// <summary>
    /// Everything the scenario created that owns an unmanaged or pooled resource, in creation order.
    /// </summary>
    /// <remarks>
    /// A list rather than named slots. The seam scenario builds a second factory and a second
    /// provider - to redeem the checkpointed handle the way a resumed run does - and named slots
    /// invited the bug they produced: a null-coalescing assignment kept the first and silently
    /// dropped the second, leaking a client and a run-scoped memo per scenario.
    /// </remarks>
    private readonly List<IDisposable> _tracked = [];

    /// <summary>Registers a disposable for scenario teardown. Nulls are ignored, so callers need no guard.</summary>
    public void Track(IDisposable? disposable)
    {
        if (disposable is not null)
        {
            _tracked.Add(disposable);
        }
    }

    /// <summary>Gets or sets the outcome of the first submission.</summary>
    public DocumentIngestResult? FirstResult { get; set; }

    /// <summary>Gets or sets the outcome of the second, duplicate submission.</summary>
    public DocumentIngestResult? DuplicateResult { get; set; }

    /// <summary>Gets or sets the prepared content the seam scenario attaches to an agent call.</summary>
    public DocumentContent? PreparedContent { get; set; }

    /// <summary>
    /// Gets or sets the endpoint the agent factory resolved from configuration.
    /// </summary>
    /// <remarks>
    /// Recorded so the seam scenario can assert a whole absolute URI rather than a path suffix: a
    /// suffix match is satisfied by <c>https://attacker.example/v1/messages</c>, and this is the
    /// request that carries the extracted document to a third party.
    /// </remarks>
    public Uri? ResolvedEndpoint { get; set; }

    /// <summary>Releases the transports, then everything tracked, in reverse creation order.</summary>
    /// <remarks>
    /// Transports first: they are what the clients hold, and disposing a client while a request is in
    /// flight is the one ordering that surfaces as a spurious scenario failure. Reverse order after
    /// that, so a provider is released before the factory whose client it borrows.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        UploadTransport?.Dispose();
        MessagesTransport?.Dispose();

        for (int index = _tracked.Count - 1; index >= 0; index--)
        {
            _tracked[index].Dispose();
        }

        _tracked.Clear();
    }
}
