using System.Net;
using System.Text;

using Cbix.Providers.Anthropic;

using Microsoft.Agents.AI;

namespace Cbix.UnitTests.Providers;

/// <summary>
/// Wire-level regression tests: they build a real agent, run it, and assert on the HTTP request the
/// SDK actually produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these exist rather than assertions on <c>ResolvedEndpoint</c>.</b> The adapter sets
/// <c>ResolvedEndpoint</c> from its own options, so an assertion on it is the class agreeing with
/// itself - it cannot fail no matter what the SDK does. The claims being defended are claims about
/// the SDK: that supplying <c>ClientOptions.BaseUrl</c> beats <c>ANTHROPIC_BASE_URL</c>, that
/// supplying <c>ApiKey</c> beats <c>ANTHROPIC_API_KEY</c>, and that nulling <c>AuthToken</c>
/// suppresses the <c>Authorization</c> header <c>ANTHROPIC_AUTH_TOKEN</c> would otherwise add. A
/// future prerelease could break any of the three, and only the outbound request shows it.
/// </para>
/// <para>
/// <b>What is exercised.</b> The full production path: <c>CreateAgent</c> builds a MAF
/// <see cref="AIAgent"/>, running it goes through MAF's chat client into the Anthropic SDK, and the
/// SDK's HTTP stack terminates at the capturing handler injected through
/// <c>AnthropicProviderOptions.Transport</c>. Nothing is stubbed between the adapter and the
/// request under inspection, and no network is touched - the handler answers every call itself.
/// </para>
/// <para>
/// The transport is injected as <c>ClientOptions.HttpClient</c> rather than through the SDK's
/// <c>Handlers</c> collection: assigning <c>Handlers</c> was measured to freeze the credential and
/// endpoint state at the moment of assignment, which would make these tests pass for reasons that
/// have nothing to do with the behaviour they claim to check.
/// </para>
/// </remarks>
[Collection(AnthropicEnvironmentCollection.Name)]
public sealed class AnthropicWireTests
{
    private const string ExplicitKey = "wire-test-explicit-key-not-a-real-credential";
    private const string HostileEndpoint = "https://attacker.example.invalid";
    private const string ProxyEndpoint = "https://egress-proxy.internal.example";

    /// <summary>
    /// The messages route, as the SDK's beta service spells it.
    /// </summary>
    /// <remarks>
    /// <b>The <c>?beta=true</c> is not incidental, and it appeared here deliberately (S01-12).</b> The
    /// factory builds agents on the SDK's beta message service because the document block, its
    /// <c>file_id</c> source and its one-hour <c>cache_control</c> TTL are beta surfaces - a
    /// non-beta agent has no way to carry any of them, and attaching them to one fails silently
    /// rather than loudly. Every agent in this pipeline reads a document, so one uniform path is
    /// safer than a mixed estate in which the silent-drop trap stays reachable. What these tests
    /// actually defend - the host, the credential header, the absence of a bearer token - is
    /// unchanged by the route.
    /// </remarks>
    private const string MessagesRoute = "/v1/messages?beta=true";

    [Fact]
    public async Task OutboundRequest_GoesToTheConfiguredEndpointNotTheAmbientOne()
    {
        // The headline regression, and the most serious of the ambient hazards: with no explicit
        // endpoint the SDK was measured sending the request - API key and document content included
        // - to whatever host ANTHROPIC_BASE_URL names, with nothing in the logs to show it.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_BASE_URL", HostileEndpoint);
        scope.Set("ANTHROPIC_API_KEY", "an-ambient-developer-key");

        CapturingHandler handler = await RunOneRequestAsync();

        Assert.Equal($"https://api.anthropic.com{MessagesRoute}", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(ExplicitKey, handler.Header("X-Api-Key"));
        Assert.Null(handler.Header("Authorization"));
    }

    [Fact]
    public async Task OutboundRequest_HonoursAnExplicitlyConfiguredEndpoint()
    {
        // Pinning the endpoint must not mean hardcoding it: an approved egress proxy (design 8) is
        // a configuration change, and it has to beat the ambient variable too.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_BASE_URL", HostileEndpoint);

        CapturingHandler handler = await RunOneRequestAsync(options => options.BaseUrl = ProxyEndpoint);

        Assert.Equal($"{ProxyEndpoint}{MessagesRoute}", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(ExplicitKey, handler.Header("X-Api-Key"));
    }

    [Fact]
    public async Task OutboundRequest_SendsNoAuthorizationHeaderWhenAnAmbientAuthTokenAppears()
    {
        // Proves the second of the two AUTH_TOKEN guards - the one that keeps working if the first
        // is ever bypassed. Validate() refuses to construct while the variable is set, so the
        // variable is introduced AFTER construction here; that is also the realistic residual case,
        // since a long-lived worker process can outlive a change to its environment.
        //
        // Without the adapter's explicit `AuthToken = null`, the SDK was measured sending
        // `Authorization: Bearer <env token>` alongside `X-Api-Key`, and the API rejects requests
        // carrying two credentials - a 401 mid-run, after checkpoints.
        CapturingHandler handler = new();
        using HttpClient transport = new(handler);

        using AnthropicAgentFactory factory = new(new AnthropicProviderOptions
        {
            ApiKey = ExplicitKey,
            Transport = transport,
        });

        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_AUTH_TOKEN", "an-ambient-oauth-token");

        await SendOneRequestAsync(factory);

        Assert.Null(handler.Header("Authorization"));
        Assert.Equal(ExplicitKey, handler.Header("X-Api-Key"));
    }

    [Fact]
    public async Task OutboundRequest_UsesTheExplicitKeyWhenProfileVariablesAreSet()
    {
        // Measured suppressed: an explicit key closes the SDK's on-disk profile resolution path.
        // Asserted at the wire because the interesting failure is a profile credential *replacing*
        // the configured one - invisible from any property the adapter exposes.
        //
        // Scope: this shows the profile variables do not perturb the outbound request. It does not
        // construct a valid on-disk profile, so it is a guard against the variables being consulted
        // at all, not a full precedence test between a real profile and an explicit key.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_PROFILE", "some-profile");
        scope.Set("ANTHROPIC_CONFIG_DIR", Path.Combine(Path.GetTempPath(), "cbix-nonexistent-profile-dir"));

        CapturingHandler handler = await RunOneRequestAsync();

        Assert.Equal($"https://api.anthropic.com{MessagesRoute}", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(ExplicitKey, handler.Header("X-Api-Key"));
        Assert.Null(handler.Header("Authorization"));
    }

    /// <summary>
    /// Builds a factory over a capturing transport, runs one agent turn, and returns the handler.
    /// </summary>
    private static async Task<CapturingHandler> RunOneRequestAsync(Action<AnthropicProviderOptions>? configure = null)
    {
        CapturingHandler handler = new();
        using HttpClient transport = new(handler);

        AnthropicProviderOptions options = new()
        {
            ApiKey = ExplicitKey,
            Transport = transport,
        };

        configure?.Invoke(options);

        using AnthropicAgentFactory factory = new(options);
        await SendOneRequestAsync(factory);

        // If the handler was never reached, every assertion downstream compares against null and
        // fails - so a silently un-exercised transport cannot look like a pass.
        Assert.NotNull(handler.RequestUri);

        return handler;
    }

    private static async Task SendOneRequestAsync(AnthropicAgentFactory factory)
    {
        AIAgent agent = factory.CreateAgent("docControl", "Extract, never interpret.");

        try
        {
            await agent.RunAsync("probe");
        }
        catch (Exception)
        {
            // The canned response is minimal and a layer above may reject it. Irrelevant: the
            // handler captured the request before any response was parsed, and the request is the
            // assertion target. Swallowing here keeps a response-shape change from being reported
            // as a credential regression.
        }
    }

    /// <summary>
    /// Terminal <see cref="HttpMessageHandler"/> that records the request and answers locally, so
    /// no traffic leaves the process.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string CannedResponse = """
            {"id":"msg_probe","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
             "content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn",
             "usage":{"input_tokens":1,"output_tokens":1}}
            """;

        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        internal Uri? RequestUri { get; private set; }

        /// <summary>The captured header value, or <see langword="null"/> when it was not sent.</summary>
        internal string? Header(string name) => _headers.GetValueOrDefault(name);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;

            _headers.Clear();
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                _headers[name] = string.Join(",", values);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedResponse, Encoding.UTF8, "application/json"),
            });
        }
    }
}
