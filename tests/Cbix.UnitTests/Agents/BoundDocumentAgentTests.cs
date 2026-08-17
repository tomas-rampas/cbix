using Cbix.Core.Agents;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Cbix.UnitTests.Agents;

/// <summary>
/// Tests for the neutral document-bound callable after its relocation into <c>Cbix.Core</c>
/// (story S01-13).
/// </summary>
/// <remarks>
/// <para>
/// The wire-level behaviour - that the Claude request actually carries the document block and both
/// beta opt-ins - stays in <c>ClaudeDocumentAgentSeamTests</c>, where the provider is. What is tested
/// here is the property the relocation had to preserve: <b>the options never escape, and each call
/// gets its own</b>. That is the guarantee that made this type worth having, and it is now
/// provider-independent, so it is provable with a stub.
/// </para>
/// <para>
/// Nothing in this file references a provider adapter, which is itself part of the claim: a consumer
/// of a document-bound agent no longer needs one.
/// </para>
/// </remarks>
public sealed class BoundDocumentAgentTests
{
    [Fact]
    public async Task RunAsync_PassesFreshOptionsFromTheFactoryOnEveryCall()
    {
        // The forgery and mutability guarantee, asserted at its root. A binding that built its options
        // once and reused them would pass a naive "the document was attached" test forever, and would
        // silently stop attaching anything the moment any holder mutated the shared instance.
        int factoryCalls = 0;
        RecordingChatClient chatClient = new();

        BoundDocumentAgent bound = new(
            new ChatClientAgent(chatClient, name: "docControl"),
            () =>
            {
                factoryCalls++;
                return new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = factoryCalls });
            });

        await bound.RunAsync("first question");
        await bound.RunAsync("second question");

        Assert.Equal(2, factoryCalls);

        // The distinct caps prove the two calls carried two different options objects, and that the
        // options actually reached the model call rather than being built and dropped.
        Assert.Equal([1, 2], chatClient.ObservedMaxOutputTokens);
    }

    [Fact]
    public async Task RunAsync_PassesTheProvidersOwnOptionsTypeStraightThrough()
    {
        // The seam the adapter fills: Core declares the neutral AgentRunOptions and never inspects it,
        // so the concrete options type - and everything provider-specific inside it - stays the
        // adapter's decision. A Core type that narrowed or rebuilt this would drag provider request
        // shaping across the boundary.
        //
        // Asserted through RunAsync rather than through an accessor. The accessor version needed an
        // InternalsVisibleTo grant on all of Cbix.Core to reach a single method, and proved less: that
        // the options were BUILT correctly, not that they reached the model call.
        ChatClientAgentRunOptions provided = new(new ChatOptions { MaxOutputTokens = 4321 });
        RecordingChatClient chatClient = new();

        BoundDocumentAgent bound = new(
            new ChatClientAgent(chatClient, name: "docControl"),
            () => provided);

        await bound.RunAsync("a question");

        Assert.Equal([4321], chatClient.ObservedMaxOutputTokens);
    }

    [Fact]
    public void Constructor_RejectsMissingArguments()
    {
        AIAgent agent = new ChatClientAgent(new RecordingChatClient(), name: "docControl");

        Assert.Throws<ArgumentNullException>(() => new BoundDocumentAgent(null!, () => new AgentRunOptions()));
        Assert.Throws<ArgumentNullException>(() => new BoundDocumentAgent(agent, null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunAsync_RejectsAnEmptyPrompt(string? prompt)
    {
        // A blank prompt would reach the model as a request with a document and no question, be
        // charged for, and answer something. Refusing it costs nothing and removes a whole class of
        // "the agent returned nonsense" investigations.
        RecordingChatClient chatClient = new();

        BoundDocumentAgent bound = new(
            new ChatClientAgent(chatClient, name: "docControl"),
            () => new AgentRunOptions());

        // ThrowsAny, not Throws: ArgumentException.ThrowIfNullOrWhiteSpace raises the ArgumentException
        // subclass that fits the input - ArgumentNullException for null, ArgumentException for blank -
        // and pinning the exact type here would assert a BCL implementation detail rather than the
        // contract, which is that a blank prompt is refused.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => bound.RunAsync(prompt!));
        Assert.Empty(chatClient.ObservedMaxOutputTokens);
    }

    [Fact]
    public void Agent_IsExposedForProvenanceButCarriesNoDocument()
    {
        // The agent is public because its name is part of design 8's provenance record. The comment on
        // the property says running it directly sends no document; this pins the half that is
        // checkable - that the property is the agent it was constructed with, not a wrapper that
        // silently attaches anything.
        AIAgent agent = new ChatClientAgent(new RecordingChatClient(), name: "matrix");

        BoundDocumentAgent bound = new(agent, () => new AgentRunOptions());

        Assert.Same(agent, bound.Agent);
        Assert.Equal("matrix", bound.Agent.Name);
    }

    [Fact]
    public void CoreAssembly_CarriesTheCallableRatherThanTheAdapter()
    {
        // The relocation itself, as an assertion. The whole point of moving this type was that an
        // executor can hold a document-bound agent without any reference to a provider - so the type's
        // home is the property, and a future refactor that moved it back would break exactly this.
        Assert.Equal("Cbix.Core", typeof(BoundDocumentAgent).Assembly.GetName().Name);
        Assert.Equal(typeof(CbixWorkflowFactory).Assembly, typeof(BoundDocumentAgent).Assembly);
    }

    /// <summary>A chat client that records the output cap it was asked for and answers minimally.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly List<int> _observed = [];

        internal IReadOnlyList<int> ObservedMaxOutputTokens
        {
            get
            {
                lock (_observed)
                {
                    return [.. _observed];
                }
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (options?.MaxOutputTokens is { } cap)
            {
                lock (_observed)
                {
                    _observed.Add(cap);
                }
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("These tests never stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
