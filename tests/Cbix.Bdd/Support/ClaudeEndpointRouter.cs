namespace Cbix.Bdd.Support;

/// <summary>
/// Splits one transport between the two Claude endpoints a whole workflow run touches: the Files
/// API that ingest uploads to, and the Messages API that triage calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one transport rather than two clients.</b> The adapter's factory owns a single
/// <c>AnthropicClient</c> and shares it across the document-content profile and every agent - that
/// is the arrangement that lets uploads and model calls travel over one connection pool. A scenario
/// that ran the whole graph would therefore have to choose which endpoint its canned handler
/// answered, and the other would 404. Routing by path keeps the composition production-shaped and
/// still lets each endpoint's own recorder make its own assertions.
/// </para>
/// <para>
/// Both branches are driven through <see cref="HttpMessageInvoker"/> rather than by inheriting from
/// <c>DelegatingHandler</c>, because a delegating chain has exactly one inner handler; the point
/// here is that there are two, chosen per request.
/// </para>
/// </remarks>
public sealed class ClaudeEndpointRouter : HttpMessageHandler
{
    private const string FilesPath = "/v1/files";

    private readonly HttpMessageInvoker _files;
    private readonly HttpMessageInvoker _messages;

    /// <summary>Initialises a new <see cref="ClaudeEndpointRouter"/>.</summary>
    /// <param name="files">Handler for the Files API.</param>
    /// <param name="messages">Handler for everything else, which in this pipeline is the Messages API.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public ClaudeEndpointRouter(HttpMessageHandler files, HttpMessageHandler messages)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(messages);

        _files = new HttpMessageInvoker(files, disposeHandler: false);
        _messages = new HttpMessageInvoker(messages, disposeHandler: false);
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool isUpload = request.RequestUri is { } uri
            && uri.AbsolutePath.EndsWith(FilesPath, StringComparison.Ordinal);

        return isUpload
            ? _files.SendAsync(request, cancellationToken)
            : _messages.SendAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The invokers are released; the handlers they wrap are not, because the scenario that built
    /// them still reads their recordings after the client is gone.
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _files.Dispose();
            _messages.Dispose();
        }

        base.Dispose(disposing);
    }
}
