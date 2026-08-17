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
/// <b>What it deliberately does not do:</b> vary its answer by request <em>content</em>, parse the
/// prompt, or simulate a schema. A stub that behaved like a model would make a failing scenario
/// ambiguous between "the pipeline is wrong" and "the fake is wrong". The canned strings are
/// supplied by the caller, so a scenario that needs structured-output JSON hands it structured-output
/// JSON.
/// </para>
/// <para>
/// <b>It does answer differently by POSITION, and story S01-16 is why.</b> Once the graph holds two
/// agents - triage returning a <c>DocumentProfile</c>, DocControl returning a
/// <c>DocControlSection</c> - one canned string cannot satisfy both, because each is now parsed
/// strictly against its own contract. A caller may therefore supply an ordered sequence, and the
/// stub hands them out in call order. That is still a dumb fake: it reads nothing, decides nothing,
/// and cannot tell one agent from another. It just runs out.
/// </para>
/// <para>
/// <b>Running out throws rather than repeating the last answer.</b> A scenario that describes two
/// calls and gets three has a pipeline doing something it did not describe - most likely a retry, a
/// duplicated node, or a fan-out that was not supposed to exist yet - and every one of those is
/// worth failing on. Repeating the last answer would hide all of them behind a green run.
/// </para>
/// </remarks>
public sealed class StubChatClient : IChatClient
{
    private readonly string[] _answers;

    private int _requestCount;

    /// <summary>Initialises a stub that answers every call with one canned string.</summary>
    /// <param name="answer">The text every response carries.</param>
    /// <exception cref="ArgumentNullException"><paramref name="answer"/> is <see langword="null"/>.</exception>
    public StubChatClient(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        _answers = [answer];
        RepeatsItsLastAnswer = true;
    }

    /// <summary>Initialises a stub that answers a sequence of calls, in order.</summary>
    /// <param name="answers">
    /// The replies, in the order the run will ask for them. For the Sprint 01 graph that is triage's
    /// profile first and the DocControl section second.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="answers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="answers"/> is empty or contains a null.</exception>
    public StubChatClient(params string[] answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        if (answers.Length == 0)
        {
            throw new ArgumentException("A stub with no answers cannot stand in for a model.", nameof(answers));
        }

        if (Array.IndexOf(answers, null!) >= 0)
        {
            throw new ArgumentException("A canned answer must not be null.", nameof(answers));
        }

        _answers = [.. answers];
        RepeatsItsLastAnswer = false;
    }

    /// <summary>Gets how many times the client was asked for a response.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>
    /// Gets a value indicating whether this stub answers every call with the same string.
    /// </summary>
    /// <remarks>
    /// True only for the single-answer constructor, which several scenarios still use because the
    /// thing they assert - that a duplicate submission never reaches an agent at all - is about the
    /// call count rather than about what came back.
    /// </remarks>
    public bool RepeatsItsLastAnswer { get; }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// A sequenced stub was asked for more replies than the scenario supplied.
    /// </exception>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        int call = Interlocked.Increment(ref _requestCount);

        if (call > _answers.Length && !RepeatsItsLastAnswer)
        {
            throw new InvalidOperationException(
                $"The run asked for model reply {call}, but the scenario supplied {_answers.Length}. The "
                    + "pipeline is making a call the scenario did not describe - a retry, a duplicated node, "
                    + "or a fan-out that should not exist yet.");
        }

        string answer = _answers[Math.Min(call, _answers.Length) - 1];

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
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
