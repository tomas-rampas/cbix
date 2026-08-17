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
        Assert.Throws<ArgumentNullException>(() => new BoundDocumentAgent(agent, (Func<AgentRunOptions>)null!));

        // The chat-attached overload (story S01-14) gets the same treatment. The null-typed literal is
        // spelled out because both overloads take two arguments and `null!` alone is ambiguous - which
        // is itself the reason to assert on both: a future refactor that collapsed them would have to
        // decide what a null second argument means, and here it means the same thing either way.
        Assert.Throws<ArgumentNullException>(() => new BoundDocumentAgent(null!, (IReadOnlyList<AIContent>)[new TextContent("page 1")]));
        Assert.Throws<ArgumentNullException>(() => new BoundDocumentAgent(agent, (IReadOnlyList<AIContent>)null!));
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

    [Fact]
    public async Task RunAsync_PutsTheChatAttachedDocumentAheadOfThePrompt()
    {
        // The chat-attached path (story S01-14): the profiles Core can build present a document as
        // ordinary AIContent, and ordinary content belongs in the turn rather than in provider run
        // options. Two properties are asserted, and the ORDER is the one that costs money if it is
        // wrong: a prompt cache keys on a prefix, so a turn whose document trailed its question would
        // miss on every call while extracting perfectly well.
        RecordingChatClient chatClient = new();

        BoundDocumentAgent bound = new(
            new ChatClientAgent(chatClient, name: "triage"),
            (IReadOnlyList<AIContent>)[new TextContent("--- Page 1 of 1 ---"), new TextContent("page text")]);

        await bound.RunAsync("Identify this document.");

        ChatMessage sent = Assert.Single(chatClient.ObservedMessages);

        Assert.Equal(ChatRole.User, sent.Role);
        Assert.Equal(
            ["--- Page 1 of 1 ---", "page text", "Identify this document."],
            sent.Contents.OfType<TextContent>().Select(text => text.Text));
    }

    [Fact]
    public async Task RunAsync_BuildsAFreshChatAttachedTurnPerCall()
    {
        // One prepared document is shared by the concurrent section fan-out, so a binding that mutated
        // or reused one message list would have six agents writing into what the seventh is reading.
        // The blocks themselves are shared deliberately - copying page images per call would be the
        // opposite mistake - so what has to be per-call is the list, and this is what says so.
        RecordingChatClient chatClient = new();

        BoundDocumentAgent bound = new(
            new ChatClientAgent(chatClient, name: "triage"),
            (IReadOnlyList<AIContent>)[new TextContent("page text")]);

        await bound.RunAsync("first question");
        await bound.RunAsync("second question");

        Assert.Equal(2, chatClient.ObservedMessages.Count);
        Assert.NotSame(chatClient.ObservedMessages[0], chatClient.ObservedMessages[1]);
        Assert.Equal(
            ["page text", "first question"],
            chatClient.ObservedMessages[0].Contents.OfType<TextContent>().Select(text => text.Text));
        Assert.Equal(
            ["page text", "second question"],
            chatClient.ObservedMessages[1].Contents.OfType<TextContent>().Select(text => text.Text));
    }

    [Fact]
    public void Constructor_RefusesAnEmptyOrHollowChatAttachedDocument()
    {
        // An empty block list produces a call that reads as document-carrying and shows the model
        // nothing - the exact failure this type exists to make impossible, arriving through the door
        // the type itself opened.
        AIAgent agent = new ChatClientAgent(new RecordingChatClient(), name: "triage");

        Assert.Throws<ArgumentException>(() => new BoundDocumentAgent(agent, (IReadOnlyList<AIContent>)[]));
        Assert.Throws<ArgumentException>(() => new BoundDocumentAgent(agent, (IReadOnlyList<AIContent>)[null!]));
    }

    [Fact]
    public void Constructor_RefusesABlockWhoseOnlyPayloadIsAProviderRawRepresentation()
    {
        // MEASURED, and it is the whole reason this guard exists. `new AIContent { RawRepresentation
        // = ... }` is exactly what the Claude native-PDF profile produces, and it is the shape MAF was
        // measured silently DROPPING from a chat turn. Without this check that content passed every
        // other test here, the request went out with no document in it, the model answered fluently
        // about a document it had never seen, and triage reported a profile ABOVE the review
        // threshold - so nothing failed, nothing was logged, and no human was ever asked.
        //
        // The exact-type comparison is the substance: a block with no neutral payload is the only one
        // a chat turn genuinely cannot express, and every derived type has one.
        AIAgent agent = new ChatClientAgent(new RecordingChatClient(), name: "triage");

        AIContent rawOnly = new() { RawRepresentation = new object() };

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new BoundDocumentAgent(agent, (IReadOnlyList<AIContent>)[rawOnly]));

        Assert.Equal("documentBlocks", error.ParamName);
        Assert.Contains("no neutral content", error.Message, StringComparison.Ordinal);

        // It refuses a raw-only block anywhere in the list, not merely as the first one: a vision
        // profile's text-then-image sequence with one provider block spliced in is the realistic
        // shape, and checking only the head would pass it.
        Assert.Throws<ArgumentException>(() => new BoundDocumentAgent(
            agent,
            (IReadOnlyList<AIContent>)[new TextContent("page text"), rawOnly]));
    }

    [Fact]
    public void Constructor_StillAcceptsBlocksThatCarryBothNeutralContentAndARawRepresentation()
    {
        // The guard must not become "refuses anything with a RawRepresentation". A TextContent or a
        // DataContent that also carries a provider payload has something a chat turn can express, so
        // it travels fine - and refusing it would break the vision and text-only profiles, which are
        // the whole agnosticism fallback.
        AIAgent agent = new ChatClientAgent(new RecordingChatClient(), name: "triage");

        TextContent annotated = new("page text") { RawRepresentation = new object() };

        BoundDocumentAgent bound = new(agent, (IReadOnlyList<AIContent>)[annotated]);

        Assert.Same(agent, bound.Agent);
    }

    [Fact]
    public async Task RunAsync_LeavesTheOutputCapUnsetOnTheChatAttachedPath()
    {
        // The narrow claim this recorder can support: nothing on the chat-attached path sets an output
        // cap, so whatever the agent was built with governs. It is NOT a claim that no options object
        // reaches the client - the recorder observes MaxOutputTokens, not the presence of options, and
        // an earlier version of this comment overstated it. What it pins is the property that matters
        // here: this path adds nothing per call, so there is nothing per call for a holder to corrupt.
        RecordingChatClient chatClient = new();

        BoundDocumentAgent bound = new(
            new ChatClientAgent(chatClient, name: "triage"),
            (IReadOnlyList<AIContent>)[new TextContent("page text")]);

        await bound.RunAsync("a question");

        Assert.Empty(chatClient.ObservedMaxOutputTokens);
    }

    /// <summary>A chat client that records the output cap it was asked for and answers minimally.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly List<int> _observed = [];
        private readonly List<ChatMessage> _messages = [];

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

        /// <summary>Gets the user turns the client was asked to send, in order.</summary>
        /// <remarks>
        /// Only user turns: a <c>ChatClientAgent</c> prepends its own system instructions, and a test
        /// asserting on "the first message" would be asserting on whether the agent had instructions.
        /// </remarks>
        internal IReadOnlyList<ChatMessage> ObservedMessages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
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

            lock (_messages)
            {
                _messages.AddRange((messages ?? []).Where(message => message.Role == ChatRole.User));
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
