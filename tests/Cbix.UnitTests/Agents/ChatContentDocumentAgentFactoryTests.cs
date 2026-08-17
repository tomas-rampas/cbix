using Cbix.Core.Agents;
using Cbix.Core.Documents;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Cbix.UnitTests.Agents;

/// <summary>
/// Core's default document-binding seam (story S01-14).
/// </summary>
/// <remarks>
/// This is the implementation that makes the whole pipeline runnable with no provider package on the
/// path: the text-only and generic-vision profiles present a document as ordinary
/// <see cref="AIContent"/>, and ordinary content is what a chat turn carries. Everything it must
/// NOT do is as load-bearing as what it does - it inspects nothing, branches on nothing, and knows
/// which profile prepared the content only in the sense that it never asks.
/// </remarks>
public sealed class ChatContentDocumentAgentFactoryTests
{
    [Fact]
    public async Task CreateForDocument_BindsThePreparedBlocksToTheAgentsTurn()
    {
        RecordingChatClient chatClient = new();
        ChatContentDocumentAgentFactory factory =
            new(new ChatClientAgent(chatClient, name: "triage"));

        BoundDocumentAgent bound = factory.CreateForDocument(
            BuildContent(new TextContent("--- Page 1 of 1 ---"), new TextContent("page text")));

        await bound.RunAsync("Identify this document.");

        Assert.Equal(
            ["--- Page 1 of 1 ---", "page text", "Identify this document."],
            Assert.Single(chatClient.UserTurns).Contents.OfType<TextContent>().Select(text => text.Text));
    }

    [Fact]
    public void CreateForDocument_ReusesOneAgentAcrossDocuments()
    {
        // An agent is a stateless caller over a client; the per-document state is the binding. A factory
        // that built a fresh agent per document would allocate a client wrapper per run for nothing, and
        // - worse on a provider path - would make "which agent produced this" a different answer each
        // time it was asked, which design 8's provenance record depends on.
        AIAgent agent = new ChatClientAgent(new RecordingChatClient(), name: "triage");
        ChatContentDocumentAgentFactory factory = new(agent);

        BoundDocumentAgent first = factory.CreateForDocument(BuildContent(new TextContent("one")));
        BoundDocumentAgent second = factory.CreateForDocument(BuildContent(new TextContent("two")));

        Assert.NotSame(first, second);
        Assert.Same(agent, first.Agent);
        Assert.Same(agent, second.Agent);
    }

    [Fact]
    public void CreateForDocument_RefusesNativeProfileContentInsteadOfDroppingIt()
    {
        // The misconfiguration this factory actually receives: a host that configured a
        // native-document capability and did not register its adapter's factory, so the native
        // profile's content arrives here. Measured before the guard existed - MAF dropped the
        // raw-only block, the model answered about a document it was never shown, and triage reported
        // a profile above the review threshold, so no human was ever asked.
        //
        // Asserted at THIS surface as well as on the constructor it delegates to, because this is the
        // type the composition names and the type a reader checks when they ask "what happens if the
        // wrong content lands here".
        ChatContentDocumentAgentFactory factory =
            new(new ChatClientAgent(new RecordingChatClient(), name: "triage"));

        DocumentContent nativeShaped = new(
            [new AIContent { RawRepresentation = new object() }],
            new DocumentContentCapabilities("claude-native-pdf", includesVisualContent: true, isDegraded: false),
            new DocumentContentHandle("sha256:" + new string('a', 64), "claude-native-pdf", "file_canned"));

        ArgumentException error = Assert.Throws<ArgumentException>(() => factory.CreateForDocument(nativeShaped));

        Assert.Contains("no neutral content", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_AndCreate_RejectMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ChatContentDocumentAgentFactory(null!));

        ChatContentDocumentAgentFactory factory =
            new(new ChatClientAgent(new RecordingChatClient(), name: "triage"));

        Assert.Throws<ArgumentNullException>(() => factory.CreateForDocument(null!));
    }

    [Fact]
    public void CoreAssembly_CarriesTheDefaultBinding()
    {
        // The property that makes the agnosticism run possible at all: the default way a document
        // reaches a model is declared in Core, so a graph with no adapter anywhere still shows the
        // model its document rather than silently sending a question about nothing.
        Assert.Equal("Cbix.Core", typeof(ChatContentDocumentAgentFactory).Assembly.GetName().Name);
        Assert.Equal(
            typeof(IDocumentBoundAgentFactory).Assembly,
            typeof(ChatContentDocumentAgentFactory).Assembly);
    }

    private static DocumentContent BuildContent(params AIContent[] blocks) =>
        new(
            blocks,
            new DocumentContentCapabilities("text-only", includesVisualContent: false, isDegraded: true),
            new DocumentContentHandle("sha256:" + new string('a', 64), "text-only", providerToken: null));

    /// <summary>A chat client that records the user turns it was asked to send.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly List<ChatMessage> _userTurns = [];

        internal IReadOnlyList<ChatMessage> UserTurns
        {
            get
            {
                lock (_userTurns)
                {
                    return [.. _userTurns];
                }
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            lock (_userTurns)
            {
                _userTurns.AddRange((messages ?? []).Where(message => message.Role == ChatRole.User));
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
