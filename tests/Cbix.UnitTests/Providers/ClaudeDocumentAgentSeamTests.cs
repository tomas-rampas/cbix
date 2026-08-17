using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Anthropic.Models.Beta.Messages;

using Cbix.Core.Agents;
using Cbix.Core.Documents;
using Cbix.Core.Ingest;
using Cbix.Providers.Anthropic;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Cbix.UnitTests.Providers;

/// <summary>
/// Wire-level tests for the document-attachment seam (story S01-12): the thing that turns content
/// prepared by the port into a request the model actually sees.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every assertion here is on the outbound request body, and that is the whole point.</b> The
/// failure this seam exists to prevent is invisible everywhere else: MAF's Anthropic integration
/// drops an <c>AIContent</c> whose only payload is <c>RawRepresentation</c>, so a request can be
/// sent, answered and parsed with no document in it and nothing anywhere reporting a problem. Only
/// the bytes on the wire distinguish a working attachment from a confident hallucination.
/// </para>
/// <para>
/// The path exercised is the production one end to end: the real profile prepares against a canned
/// Files API, the real factory builds a real agent, and running it goes through MAF into the SDK and
/// terminates at the capturing handler.
/// </para>
/// </remarks>
[Collection(AnthropicEnvironmentCollection.Name)]
public sealed class ClaudeDocumentAgentSeamTests : IDisposable
{
    private const string ExplicitKey = "seam-test-explicit-key-not-a-real-credential";
    private const string HaikuSnapshot = "claude-haiku-4-5-20251001";
    private const string SonnetSnapshot = "claude-sonnet-4-5-20250929";

    private readonly string _root = Directory.CreateTempSubdirectory("cbix-claude-seam-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a suite over.
        }
    }

    [Fact]
    public async Task DocumentAgent_SendsTheDocumentBlockAndBothBetaOptIns()
    {
        using Seam seam = await Seam.CreateAsync(this);

        BoundDocumentAgent bound = seam.Factory.CreateDocumentAgent(
            "docControl",
            "Extract, never interpret.",
            seam.Document);

        await RunAsync(bound);

        JsonElement block = FindDocumentBlock(seam.Body());
        Assert.Equal("file", block.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(seam.FileId, block.GetProperty("source").GetProperty("file_id").GetString());

        // The TTL the profile marks the block with, and the beta that accompanies it. The strength of
        // that pairing is SDK-model-derived rather than live-verified (see BuildRequest's remarks);
        // what these assertions pin is that we send both, which is the safe direction - an
        // unnecessary header costs nothing, a missing required one fails the call.
        Assert.Equal("1h", block.GetProperty("cache_control").GetProperty("ttl").GetString());
        Assert.Contains("extended-cache-ttl-2025-04-11", seam.Messages.BetaHeader!, StringComparison.Ordinal);
        Assert.Contains("files-api-2025-04-14", seam.Messages.BetaHeader!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentAgent_PlacesTheDocumentBeforeTheCallersTurn()
    {
        // Order is load-bearing, not cosmetic: a prompt cache matches on a prefix, so a document that
        // arrives after the caller's question leaves the cached prefix ending before the expensive
        // part and makes every later call a miss.
        using Seam seam = await Seam.CreateAsync(this);

        BoundDocumentAgent bound = seam.Factory.CreateDocumentAgent(
            "docControl",
            "Extract, never interpret.",
            seam.Document);

        await RunAsync(bound, "Extract the document control block.");

        JsonElement messages = seam.Body().GetProperty("messages");
        Assert.True(messages.GetArrayLength() >= 2, "The request should carry the document turn and the caller's turn.");

        JsonElement first = messages[0].GetProperty("content")[0];
        Assert.Equal("document", first.GetProperty("type").GetString());

        JsonElement last = messages[messages.GetArrayLength() - 1].GetProperty("content")[0];
        Assert.Equal("text", last.GetProperty("type").GetString());
        Assert.Equal("Extract the document control block.", last.GetProperty("text").GetString());
    }

    [Fact]
    public async Task DocumentAgent_AppliesOneResolvedModelToBothTheAgentAndTheRequest()
    {
        // The hazard this closes, measured: the values on the object returned by
        // RawRepresentationFactory OVERRIDE the agent's own chat options on the wire. Split across
        // two calls, a Sonnet agent could be run on whatever the attachment happened to name, and the
        // only symptom would be degraded matrix accuracy months later.
        using Seam seam = await Seam.CreateAsync(this);

        BoundDocumentAgent bound = seam.Factory.CreateDocumentAgent(
            "matrix",
            "Read the permission matrix.",
            seam.Document,
            modelId: SonnetSnapshot,
            maxOutputTokens: 1234);

        await RunAsync(bound);

        JsonElement body = seam.Body();
        Assert.Equal(SonnetSnapshot, body.GetProperty("model").GetString());
        Assert.Equal(1234, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task DocumentAgent_KeepsTheAgentsInstructionsAsTheSystemPrompt()
    {
        // Guards against the attachment quietly replacing what the agent was built with: the raw
        // request wins on model and tokens, so it is worth proving it does not also win on the system
        // prompt - the extraction rules are the agent's contract.
        using Seam seam = await Seam.CreateAsync(this);

        BoundDocumentAgent bound = seam.Factory.CreateDocumentAgent(
            "docControl",
            "Extract, never interpret. Copy snippets verbatim.",
            seam.Document);

        await RunAsync(bound);

        JsonElement system = seam.Body().GetProperty("system");
        Assert.Contains(
            "Extract, never interpret.",
            system[0].GetProperty("text").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentAgent_RefusesContentFromAnotherProfile()
    {
        // Content from the vision or text-only profile travels as ordinary chat content. Routing it
        // through this hatch would drop it - request sent, answer returned, nobody told - so the
        // mismatch is refused before any call is made.
        using Seam seam = await Seam.CreateAsync(this);

        DocumentContent foreign = new(
            [new TextContent("page one text")],
            new DocumentContentCapabilities("text-only", includesVisualContent: false, isDegraded: true),
            new DocumentContentHandle(seam.Document.Handle.DocumentId, "text-only"));

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => seam.Factory.CreateDocumentAgent("docControl", "Extract, never interpret.", foreign));

        Assert.Contains("text-only", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, seam.Messages.RequestCount);
    }

    [Fact]
    public async Task DocumentAgent_RefusesAFloatingModelAlias()
    {
        // The same guard CreateAgent applies, on the path that would otherwise bypass it: an alias
        // here would reach the wire through the raw request and beat the agent's pinned snapshot.
        using Seam seam = await Seam.CreateAsync(this);

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => seam.Factory.CreateDocumentAgent(
                "matrix",
                "Read the permission matrix.",
                seam.Document,
                modelId: "claude-sonnet-4-5"));

        Assert.Contains("dated model snapshot", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentAgent_RunOptionsAreReusableAcrossCalls()
    {
        // One agent answers several questions about its document, and each call must present the same
        // document prefix - which is what the prompt cache matches on.
        using Seam seam = await Seam.CreateAsync(this);

        BoundDocumentAgent bound = seam.Factory.CreateDocumentAgent(
            "docControl",
            "Extract, never interpret.",
            seam.Document);

        await RunAsync(bound, "First question.");
        string? firstFileId = FindDocumentBlock(seam.Body()).GetProperty("source").GetProperty("file_id").GetString();

        await RunAsync(bound, "Second question.");
        string? secondFileId = FindDocumentBlock(seam.Body()).GetProperty("source").GetProperty("file_id").GetString();

        Assert.Equal(2, seam.Messages.RequestCount);
        Assert.Equal(seam.FileId, firstFileId);
        Assert.Equal(firstFileId, secondFileId);

        // Exactly one document block per call. A binding that accumulated blocks across calls would
        // re-send the document every turn and pay full input price for it - the opposite of what the
        // cache marker is for - while still passing every assertion above.
        Assert.Equal(1, CountDocumentBlocks(seam.Body()));
    }

    [Fact]
    public async Task DocumentAgent_ExposesNoWayToObtainTheRunOptions()
    {
        // The forgery guarantee, asserted the strongest way available: there is no member on the
        // binding that hands anyone the options at all.
        //
        // This replaces a test that reached CreateRunOptions through an InternalsVisibleTo grant and
        // sabotaged one instance to prove the next call was unaffected. That test asserted something
        // weaker than what is true - "a holder cannot spoil the next call" - while requiring Core to
        // expose every one of its internals to the test assembly. The property is better stated as
        // "nobody can become a holder", and stating it that way costs no grant.
        //
        // Reflection over every member kind, because the leak this guards against would be a
        // convenience accessor added later by someone who found the private method inconvenient.
        const BindingFlags Surface =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.DeclaredOnly;

        List<string> exposed =
        [
            .. typeof(BoundDocumentAgent).GetMethods(Surface)
                .Where(method => !method.IsPrivate && typeof(AgentRunOptions).IsAssignableFrom(method.ReturnType))
                .Select(method => $"method {method.Name}"),
            .. typeof(BoundDocumentAgent).GetProperties(Surface)
                .Where(property => typeof(AgentRunOptions).IsAssignableFrom(property.PropertyType))
                .Select(property => $"property {property.Name}"),
            .. typeof(BoundDocumentAgent).GetFields(Surface)
                .Where(field => !field.IsPrivate && typeof(AgentRunOptions).IsAssignableFrom(field.FieldType))
                .Select(field => $"field {field.Name}"),
        ];

        Assert.True(
            exposed.Count == 0,
            $"BoundDocumentAgent hands out its run options through: {string.Join(", ", exposed)}. The options "
                + "carry the document attachment, and a holder that nulled the raw-representation factory "
                + "would silently stop sending the document from then on.");

        // And the behaviour that matters, through the public path: two calls, two documents on the
        // wire. This is what the sabotage test was really proving, and it proves it against the same
        // route production uses rather than against an accessor only a test can reach.
        using Seam seam = await Seam.CreateAsync(this);

        BoundDocumentAgent bound = seam.Factory.CreateDocumentAgent(
            "docControl",
            "Extract, never interpret.",
            seam.Document);

        await RunAsync(bound, "First question.");
        Assert.Equal(seam.FileId, FindDocumentBlock(seam.Body()).GetProperty("source").GetProperty("file_id").GetString());

        await RunAsync(bound, "Second question.");
        Assert.Equal(seam.FileId, FindDocumentBlock(seam.Body()).GetProperty("source").GetProperty("file_id").GetString());
    }

    [Fact]
    public async Task RawRequestValuesOverrideTheAgentsOwnChatOptions()
    {
        // Pins the measured precedence the whole design rests on. If a future package version made
        // the AGENT's model win instead, CreateDocumentAgent's "resolve once, hand to both" would
        // still be correct but its stated reason would be wrong - and the next person to read it
        // would draw the wrong conclusion. Reached through internal access because no supported
        // public shape can express the mismatch any more, which is the point.
        using Seam seam = await Seam.CreateAsync(this);

        AIAgent agent = seam.Factory.CreateAgent("docControl", "Extract, never interpret.", modelId: HaikuSnapshot);

        IReadOnlyList<BetaContentBlockParam> blocks = ClaudeDocumentAttachment.DescribeBlocks(seam.Document);
        ChatClientAgentRunOptions mismatched = new(new ChatOptions
        {
            RawRepresentationFactory = _ => ClaudeDocumentAttachment.BuildRequest(blocks, SonnetSnapshot, 4321),
        });

        try
        {
            await agent.RunAsync("probe", options: mismatched);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Resource exhaustion propagates as itself - the codebase rule the ingest ports state.
        }

        JsonElement body = seam.Body();
        Assert.Equal(SonnetSnapshot, body.GetProperty("model").GetString());
        Assert.Equal(4321, body.GetProperty("max_tokens").GetInt32());
    }

    /// <summary>Runs one agent turn, swallowing response-shape complaints the canned reply may cause.</summary>
    private static async Task RunAsync(BoundDocumentAgent bound, string prompt = "probe")
    {
        try
        {
            await bound.RunAsync(prompt);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Resource exhaustion propagates as itself - the codebase rule the ingest ports state.
            // The request was captured before any response was parsed, and the request is the
            // assertion target. Swallowing keeps a response-shape change from being reported as an
            // attachment regression.
        }
    }

    /// <summary>Counts document blocks across every message in an outbound request.</summary>
    private static int CountDocumentBlocks(JsonElement body)
    {
        int count = 0;

        foreach (JsonElement message in body.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("content").ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement block in message.GetProperty("content").EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement type)
                    && string.Equals(type.GetString(), "document", StringComparison.Ordinal))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Finds the document block in an outbound request, failing in the story's language.</summary>
    private static JsonElement FindDocumentBlock(JsonElement body)
    {
        foreach (JsonElement message in body.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("content").ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement block in message.GetProperty("content").EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement type)
                    && string.Equals(type.GetString(), "document", StringComparison.Ordinal))
                {
                    return block;
                }
            }
        }

        Assert.Fail(
            "The outbound request carries no document block: the model would answer about a document it was "
                + "never shown, and nothing else in the system would say so.");
        throw new InvalidOperationException("Unreachable: Assert.Fail always throws.");
    }

    /// <summary>A prepared document, a factory over a capturing messages transport, and their plumbing.</summary>
    private sealed class Seam : IDisposable
    {
        private Seam(
            AnthropicAgentFactory factory,
            HttpClient messagesTransport,
            CapturingMessages messages,
            DocumentContent document,
            string fileId)
        {
            Factory = factory;
            MessagesTransport = messagesTransport;
            Messages = messages;
            Document = document;
            FileId = fileId;
        }

        internal AnthropicAgentFactory Factory { get; }

        internal CapturingMessages Messages { get; }

        internal DocumentContent Document { get; }

        internal string FileId { get; }

        private HttpClient MessagesTransport { get; }

        internal static async Task<Seam> CreateAsync(ClaudeDocumentAgentSeamTests owner)
        {
            ArgumentNullException.ThrowIfNull(owner);

            // The document is prepared through the real profile against a canned Files API, so the
            // block under test is the one the pipeline actually produces rather than one written here.
            CannedFiles files = new();
            using HttpClient uploadTransport = new(files);
            using AnthropicAgentFactory uploadFactory = new(new AnthropicProviderOptions
            {
                ApiKey = ExplicitKey,
                Transport = uploadTransport,
            });

            IDocumentContentProvider provider = uploadFactory.CreateDocumentContentProvider();

            // A real digest, because the profile verifies it before uploading: the identity a
            // registry publishes IS the content hash, and a fixture that invented one would be
            // testing a path production can never take.
            const string Body = "%PDF-1.7\nspecimen\n%%EOF\n";
            string path = Path.Combine(owner._root, "de-specimen.pdf");
            await File.WriteAllTextAsync(path, Body);

            ContentHash identity = ContentHash.ComputeSha256(Encoding.UTF8.GetBytes(Body));

            DocumentContent document = await provider.PrepareAsync(
                new DocumentReference(identity.Canonical, new Uri(path), "de-specimen.pdf"));

            (provider as IDisposable)?.Dispose();

            CapturingMessages messages = new();
            HttpClient messagesTransport = new(messages);
            AnthropicAgentFactory factory = new(new AnthropicProviderOptions
            {
                ApiKey = ExplicitKey,
                ModelId = HaikuSnapshot,
                Transport = messagesTransport,
            });

            return new Seam(factory, messagesTransport, messages, document, CannedFiles.FileId);
        }

        internal JsonElement Body()
        {
            Assert.True(Messages.RequestCount > 0, "No message request reached the transport.");
            Assert.NotNull(Messages.Body);

            return JsonDocument.Parse(Messages.Body!).RootElement;
        }

        public void Dispose()
        {
            MessagesTransport.Dispose();
            Factory.Dispose();
        }
    }

    /// <summary>Canned Files API: answers an upload so the profile can produce a real block.</summary>
    private sealed class CannedFiles : HttpMessageHandler
    {
        internal const string FileId = "file_seam_canned_specimen";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {"id":"{{FileId}}","type":"file","filename":"de-specimen.pdf","mime_type":"application/pdf",
                     "size_bytes":24,"created_at":"2026-01-01T00:00:00Z","downloadable":false}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    /// <summary>Capturing Messages API: records the outbound request and answers minimally.</summary>
    private sealed class CapturingMessages : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal string? Body { get; private set; }

        internal string? BetaHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            BetaHeader = request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? values)
                ? string.Join(",", values)
                : null;

            if (request.Content is { } content)
            {
                Body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"id":"msg_seam","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
                     "content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn",
                     "usage":{"input_tokens":1,"output_tokens":1}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
