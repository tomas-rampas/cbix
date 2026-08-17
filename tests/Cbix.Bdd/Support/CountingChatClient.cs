using Microsoft.Extensions.AI;

namespace Cbix.Bdd.Support;

/// <summary>
/// A chat client that answers everything with one canned string and counts how often it was asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the entire provider side of the topology scenarios.</b> The graph is built against
/// <c>AIAgent</c>, an agent is built against <see cref="IChatClient"/>, so anything satisfying this
/// interface drives the whole pipeline - no SDK, no credential, no network. That is what makes the
/// topology story's scenarios also the shape S01-09 turns into the permanent agnosticism gate.
/// </para>
/// <para>
/// <b>The count is the assertion, not a diagnostic.</b> "The triage agent is not called" for a
/// duplicate submission cannot be proved by the run's output - a run that ends early and a run that
/// ends early <em>after</em> paying for a model call look identical from outside. Counting the calls
/// is what distinguishes them, and the thing being protected is money.
/// </para>
/// </remarks>
public sealed class CountingChatClient(string answer) : IChatClient
{
    private int _requestCount;

    /// <summary>Gets how many times the client was asked for a response.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _requestCount);

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The topology scenarios never stream.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to release: the canned answer is a string and the counter is an int.
    }
}
