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
    private readonly string[] _assistantTexts = [DefaultAssistantText];
    private readonly bool _repeatsItsLastAnswer = true;

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

        _assistantTexts = [assistantText];
    }

    /// <summary>Initialises a handler that answers a sequence of calls, in order.</summary>
    /// <param name="assistantTexts">
    /// The replies, in the order the run will ask for them. For the Sprint 01 graph that is triage's
    /// profile first and the DocControl section second.
    /// </param>
    /// <remarks>
    /// <b>Added for story S01-16, and for the same reason the stub chat client grew one.</b> Once the
    /// graph holds two agents whose replies are each parsed strictly against their own contract, one
    /// canned answer cannot satisfy both - the triage profile handed to DocControl is refused, and the
    /// run dies before the wire assertion has anything to look at. Answering by position keeps the fake
    /// dumb: it reads nothing and cannot tell one agent from another.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="assistantTexts"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="assistantTexts"/> is empty or contains a null.</exception>
    public CapturingMessagesHandler(params string[] assistantTexts)
    {
        ArgumentNullException.ThrowIfNull(assistantTexts);

        if (assistantTexts.Length == 0)
        {
            throw new ArgumentException("A canned transport with no answers cannot stand in for the API.", nameof(assistantTexts));
        }

        if (Array.IndexOf(assistantTexts, null!) >= 0)
        {
            throw new ArgumentException("A canned answer must not be null.", nameof(assistantTexts));
        }

        _assistantTexts = [.. assistantTexts];
        _repeatsItsLastAnswer = false;
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

        // The RETURN VALUE, not a re-read of the field. Two calls in flight could otherwise both read
        // the post-increment count and answer with the same canned reply while reporting different
        // ones - a race that today is theoretical (the Sprint 01 graph calls agents strictly in
        // sequence) and stops being so the moment Sprint 02's seven-way fan-out lands. Taking the
        // increment's own answer makes each call's position its own, whatever else is running.
        int call = Interlocked.Increment(ref _requestCount);

        // NOT thread-safe, and knowingly so: RequestUri, BetaHeader and Body are "the most recent
        // request", which is a coherent idea only while requests are sequential. Under a parallel
        // fan-out they become a race whose loser's request is simply lost, so a scenario asserting on
        // them will need per-request capture (a list keyed by call number) before Sprint 02 widens the
        // graph. Recorded here rather than discovered as a flaky assertion.
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
            Content = new StringContent(BuildResponse(call), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>The reply for the call being answered, by position.</summary>
    /// <remarks>
    /// Running out throws rather than repeating: a scenario that describes two calls and gets three
    /// has a pipeline doing something it did not describe - a retry, a duplicated node, a fan-out that
    /// should not exist yet - and every one of those is worth failing on.
    /// </remarks>
    private string AnswerFor(int call)
    {
        if (call > _assistantTexts.Length && !_repeatsItsLastAnswer)
        {
            throw new InvalidOperationException(
                $"The run asked for model reply {call}, but the scenario supplied {_assistantTexts.Length}.");
        }

        return _assistantTexts[Math.Min(call, _assistantTexts.Length) - 1];
    }

    /// <summary>Builds a Messages API response in the shape the SDK deserialises.</summary>
    /// <param name="call">This request's position, taken from its own increment.</param>
    private string BuildResponse(int call) =>
        JsonSerializer.Serialize(new
        {
            id = "msg_bdd_canned",
            type = "message",
            role = "assistant",
            model = "claude-haiku-4-5-20251001",
            content = new[] { new { type = "text", text = AnswerFor(call) } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 1, output_tokens = 1 },
        });
}
