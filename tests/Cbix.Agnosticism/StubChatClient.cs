using Microsoft.Extensions.AI;

namespace Cbix.Agnosticism;

/// <summary>
/// A chat client that answers everything with one canned string and counts how often it was asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the entire provider side of the offline scenarios.</b> The graph is built against
/// <c>AIAgent</c>, an agent is built against <see cref="IChatClient"/>, so anything satisfying this
/// interface drives the whole pipeline - no SDK, no credential, no network. Design 3 names exactly
/// that run as the acceptance criterion for LLM-agnosticism, and story S01-09 turns it into the
/// permanent gate.
/// </para>
/// <para>
/// <b>It lives in this assembly rather than in the BDD project, and that is load-bearing.</b> The
/// run executes this type, so its declaring assembly belongs to the run's dependency graph - the
/// thing S01-09's second assertion walks. <c>Cbix.Bdd</c> references the Anthropic adapter (S01-08's
/// scenarios test it), so a stub declared there would leave the walk one hop from the provider and
/// force an exclusion-by-fiat to keep the gate green. Here there is nothing to exclude.
/// </para>
/// <para>
/// <b>The count is an assertion, not a diagnostic.</b> "The triage agent is not called" for a
/// duplicate submission cannot be proved from the run's output - a run that ends early and a run
/// that ends early <em>after</em> paying for a model call look identical from outside. Counting the
/// calls is what distinguishes them, and the thing being protected is money.
/// </para>
/// <para>
/// <b>What it deliberately does not do:</b> vary its answer by request, parse the prompt, or
/// simulate a schema. A stub that behaved like a model would make a failing scenario ambiguous
/// between "the pipeline is wrong" and "the fake is wrong". The canned string is supplied by the
/// caller, so a scenario that needs structured-output JSON hands it structured-output JSON.
/// </para>
/// </remarks>
/// <param name="answer">The text every response carries.</param>
public sealed class StubChatClient(string answer) : IChatClient
{
    private int _requestCount;

    /// <summary>Gets how many times the client was asked for a response.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Gets the canned text every response carries.</summary>
    public string Answer { get; } = answer ?? throw new ArgumentNullException(nameof(answer));

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _requestCount);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer)));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Throwing rather than falling back to a synthesised stream: no CBIX agent streams today, so a
    /// call arriving here means the pipeline changed shape, and a silent fake would hide that behind
    /// a passing scenario.
    /// </remarks>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The offline scenarios never stream.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to release: the canned answer is a string and the counter is an int.
    }
}
