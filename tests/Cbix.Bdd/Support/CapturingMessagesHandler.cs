using System.Net;
using System.Text;
using System.Text.Json;

namespace Cbix.Bdd.Support;

/// <summary>
/// Terminal handler standing in for the Messages API: it records the outbound request and answers
/// with a minimal valid message, so an agent turn can be run without a key or a network.
/// </summary>
/// <remarks>
/// The request body is the assertion target, and it is captured before any response is parsed. That
/// matters for the seam scenario: the claim being checked is that the document block and the cache
/// beta reached the wire, and that is true or false regardless of what the model would have replied.
/// </remarks>
public sealed class CapturingMessagesHandler : DelegatingHandler
{
    private const string DefaultAssistantText = "ok";

    private readonly bool _forwards;
    private readonly string _assistantText = DefaultAssistantText;

    private int _requestCount;

    /// <summary>Initialises a handler that answers locally with a canned message.</summary>
    public CapturingMessagesHandler()
    {
    }

    /// <summary>Initialises a handler that answers locally with a caller-supplied assistant reply.</summary>
    /// <param name="assistantText">The text the canned assistant message carries.</param>
    /// <remarks>
    /// <para>
    /// Added for the triage wire scenario, which asserts two things about one call: that the
    /// document block and its beta opt-ins reached the wire, and that the reply was parsed by the
    /// production parser into a <c>DocumentProfile</c>. The default "ok" satisfies the first and
    /// defeats the second, so a scenario that needs both has to be able to say what the model said.
    /// </para>
    /// <para>
    /// The text is JSON-escaped into the canned response body, so a structured-output payload -
    /// which is full of quote characters - survives being embedded in one.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="assistantText"/> is <see langword="null"/>.</exception>
    public CapturingMessagesHandler(string assistantText)
    {
        ArgumentNullException.ThrowIfNull(assistantText);

        _assistantText = assistantText;
    }

    /// <summary>Initialises a handler that records and then forwards to the real API.</summary>
    /// <param name="innerHandler">The network stack to forward to.</param>
    /// <remarks>
    /// <b>The body is not captured on this path.</b> Reading a request's content consumes it, which
    /// would break the very call being observed - so a forwarding instance records the URI and the
    /// headers only, and a live scenario asserts on the outcome of the call rather than on its body.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="innerHandler"/> is <see langword="null"/>.</exception>
    public CapturingMessagesHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);

        _forwards = true;
    }

    /// <summary>Gets the number of message requests observed.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Gets the URI of the most recent request.</summary>
    public Uri? RequestUri { get; private set; }

    /// <summary>Gets the JSON body of the most recent request.</summary>
    public string? Body { get; private set; }

    /// <summary>Gets the <c>anthropic-beta</c> header of the most recent request.</summary>
    public string? BetaHeader { get; private set; }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Interlocked.Increment(ref _requestCount);
        RequestUri = request.RequestUri;
        BetaHeader = request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? values)
            ? string.Join(",", values)
            : null;

        if (_forwards)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (request.Content is { } content)
        {
            Body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildResponse(), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Builds a Messages API response in the shape the SDK deserialises.</summary>
    private string BuildResponse() =>
        JsonSerializer.Serialize(new
        {
            id = "msg_bdd_canned",
            type = "message",
            role = "assistant",
            model = "claude-haiku-4-5-20251001",
            content = new[] { new { type = "text", text = _assistantText } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 1, output_tokens = 1 },
        });
}
