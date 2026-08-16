using global::Anthropic;
using global::Anthropic.Core;

using Microsoft.Agents.AI;

namespace Cbix.Providers.Anthropic;

/// <summary>
/// Builds MAF <see cref="AIAgent"/> instances backed by the Anthropic Claude API, via MAF's
/// first-party <c>Microsoft.Agents.AI.Anthropic</c> integration (design 3, 7).
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is the boundary.</b> It is the only place in the solution that may name a type
/// from <c>Microsoft.Agents.AI.Anthropic</c> or from the Anthropic SDK beneath it. Everything the
/// factory hands back is framework currency - <see cref="AIAgent"/> - so the workflow, the section
/// agents and the executors cannot tell which provider is underneath, and a provider swap stays a
/// configuration change. That claim is enforced, not asserted: <c>ProviderContainmentTests</c> and
/// the S01-08 BDD scenario reflect over what the compiler emitted into every non-adapter
/// <c>Cbix.*</c> assembly and fail if any names a provider assembly.
/// </para>
/// <para>
/// <b>Raw-representation escape hatch: here and nowhere else.</b> Design 3 reaches Claude-specific
/// capabilities - native PDF mode, <c>cache_control</c> prompt caching, Files API <c>file_id</c>
/// reuse, extended thinking - through the integration's escape hatch: a
/// <c>RawRepresentationFactory</c> returning <c>MessageCreateParams</c> on the chat options, and
/// <c>AnthropicClient.Beta.Files</c> for uploads. This story needs none of them, so none appear
/// below. The rule is recorded here because it is a rule about <em>location</em>: when S01-05 adds
/// the Claude document-content profile, those call sites belong in this assembly, behind
/// <c>IDocumentContentProvider</c>, with the provider payload riding inside
/// <c>AIContent.RawRepresentation</c> - never in a signature Core can see. That rule is itself
/// checked: <c>ProviderContainmentTests</c> scans the IL member references of every non-adapter
/// assembly for raw-representation members.
/// </para>
/// <para>
/// <b>The client and its options are key-bearing - never expose either.</b> The SDK's
/// <c>ClientOptions</c> is record-like and its <c>ToString()</c> was measured printing
/// <c>ApiKey = &lt;the key&gt;</c> in clear text, so it must never be logged, serialised, or
/// included in an exception. The same applies transitively to <see cref="AnthropicClient"/>: its
/// <c>WithOptions</c> hands the caller that key-bearing struct. Both are therefore private fields
/// with no accessor, and the scalar settings this class needs are copied out at construction so
/// no reference to the options object is retained.
/// </para>
/// <para>
/// <b>Lifetime.</b> The factory owns one <see cref="AnthropicClient"/> and shares it across every
/// agent it creates, which is what lets the underlying HTTP handler pool connections. Agents
/// therefore outlive no longer than their factory: register it as a singleton and dispose it with
/// the host.
/// </para>
/// <para>
/// <b>Prerelease dependency.</b> <c>Microsoft.Agents.AI.Anthropic</c> is prerelease and both it and
/// the SDK are pinned exactly (design 11). Expect the surface used here - <c>AsAIAgent</c>'s
/// parameters, the client's options shape - to move on upgrade, and re-run the ambient-credential
/// probe described on <see cref="AnthropicProviderOptions"/> when it does. Confining all of it to
/// this file is what keeps such a change a local repair.
/// </para>
/// </remarks>
public sealed class AnthropicAgentFactory : IDisposable
{
    private readonly AnthropicClient _client;
    private readonly string _defaultModelId;
    private readonly int _defaultMaxOutputTokens;

    // Interlocked-guarded rather than a plain bool: the seven-agent parallel fan-out means a
    // CreateAgent racing host shutdown is an ordinary occurrence, not a theoretical one. `int`
    // because Interlocked has no bool overload.
    private int _disposed;

    /// <summary>Initialises the factory and its underlying Anthropic client.</summary>
    /// <param name="options">
    /// Configuration. The API key must already be resolved - this type never sources it. Validated
    /// here, including the ambient-environment guard.
    /// </param>
    /// <remarks>
    /// <para>
    /// Construction performs no I/O: no network call, no token exchange, no credential probe. That
    /// is a property the tests depend on (they build agents offline with a fictional key), and it
    /// is achieved by supplying every credential- and endpoint-bearing option explicitly. Three
    /// settings are deliberate, each backed by a measurement on the pinned SDK - see the
    /// ambient-environment table on <see cref="AnthropicProviderOptions"/>:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///   <c>ApiKey</c> - supplying it suppresses the SDK's on-disk profile resolution entirely,
    ///   so no ambient profile can override the key the secrets manager resolved.
    ///   </description></item>
    ///   <item><description>
    ///   <c>BaseUrl</c> - set explicitly because an unset endpoint was measured to be taken from
    ///   <c>ANTHROPIC_BASE_URL</c>, redirecting the key and the document content to an arbitrary
    ///   host. The explicit value wins.
    ///   </description></item>
    ///   <item><description>
    ///   <c>AuthToken</c> - explicitly nulled. An explicit API key does <em>not</em> suppress
    ///   <c>ANTHROPIC_AUTH_TOKEN</c>; without this the client sends <c>X-Api-Key</c> and
    ///   <c>Authorization: Bearer</c> together and the API rejects the pair. Assigning null was
    ///   measured to remove the stray header. <see cref="AnthropicProviderOptions.Validate"/> also
    ///   refuses to start in that situation, so this is the second of two independent guards.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The client is constructed as <c>new AnthropicClient(clientOptions)</c> rather than via an
    /// object initialiser on the client itself: the initialiser path runs the parameterless
    /// constructor first, so credential auto-resolution would already have consulted the
    /// environment and the disk before the key was assigned.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="options"/> is incomplete or out of range, or a conflicting ambient credential
    /// is present in the environment.
    /// </exception>
    public AnthropicAgentFactory(AnthropicProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        // Copied out, not retained: one less reference to a key-bearing object, and it makes the
        // factory immune to an options instance being mutated after construction.
        _defaultModelId = options.ModelId;
        _defaultMaxOutputTokens = options.MaxOutputTokens;
        ResolvedEndpoint = options.ResolveBaseUrl();

        ClientOptions clientOptions = new()
        {
            ApiKey = options.ApiKey,

            // AbsoluteUri, not ToString(): ToString() unescapes percent-encoding (a %20 in a proxy
            // path comes back as a literal space), which would corrupt an endpoint that needed the
            // escaping. AbsoluteUri round-trips it.
            BaseUrl = ResolvedEndpoint.AbsoluteUri,
            AuthToken = null,
        };

        // Assigned last and separately. ClientOptions is a struct of lazily-resolved fields, and
        // assigning the transport-related members was measured to freeze the credential and
        // endpoint state as it stands at that moment - so every credential and endpoint setting
        // above must already be in place before this runs.
        if (options.Transport is { } transport)
        {
            clientOptions.HttpClient = transport;
        }

        _client = new AnthropicClient(clientOptions);
    }

    /// <summary>
    /// Gets the endpoint every agent from this factory calls.
    /// </summary>
    /// <remarks>
    /// Exposed for two reasons. Design 8 requires that a published value can be traced to what
    /// produced it, and "which endpoint did this run talk to" is part of that record - especially
    /// once an approved egress proxy is in play. It is also the seam that lets the regression tests
    /// assert the endpoint is governed by configuration rather than by
    /// <c>ANTHROPIC_BASE_URL</c> without making a network call. Safe to log: unlike the client
    /// options it is derived from, it carries no credential.
    /// </remarks>
    public Uri ResolvedEndpoint { get; }

    /// <summary>
    /// Creates a MAF agent with the given name and instructions.
    /// </summary>
    /// <param name="name">
    /// Agent name as used in the workflow graph (for example <c>docControl</c>). It reaches
    /// telemetry and the extraction-run record, so it identifies which agent produced a candidate.
    /// </param>
    /// <param name="instructions">
    /// The agent's system instructions - the extraction prompting rules plus its section-specific
    /// contract.
    /// </param>
    /// <param name="modelId">
    /// Exact, dated model snapshot to call, or <see langword="null"/> to use
    /// <see cref="AnthropicProviderOptions.ModelId"/>. Supplied explicitly by the tiered call sites
    /// (design 7: Matrix on Sonnet, the rest on Haiku).
    /// </param>
    /// <param name="maxOutputTokens">
    /// Per-response output cap, or <see langword="null"/> to use
    /// <see cref="AnthropicProviderOptions.MaxOutputTokens"/>.
    /// </param>
    /// <returns>
    /// A MAF <see cref="AIAgent"/>. The declared type is the framework abstraction, not the
    /// integration's concrete agent class: a caller that could name the concrete type would be
    /// coupled to the provider, which is the leak this adapter exists to prevent.
    /// </returns>
    /// <remarks>
    /// <b>Forward path for Sprint 02.</b> Structured outputs (each section agent's own JSON schema)
    /// and tools (the normaliser's dictionary lookups) are configured through
    /// <c>ChatClientAgentOptions</c>, which the integration accepts via its other overload,
    /// <c>AsAIAgent(IAnthropicClient, ChatClientAgentOptions, ...)</c>. That is where those stories
    /// should extend this method - and it is also where a <c>RawRepresentationFactory</c> would be
    /// attached for Claude-specific request shaping. The scalar overload used here covers exactly
    /// what S01-08 needs, so no unused configuration surface is introduced ahead of a caller.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/>, <paramref name="instructions"/>, or a supplied
    /// <paramref name="modelId"/> is <see langword="null"/>, empty, or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxOutputTokens"/> is supplied and is not greater than zero.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The factory has been disposed.</exception>
    public AIAgent CreateAgent(
        string name,
        string instructions,
        string? modelId = null,
        int? maxOutputTokens = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);

        if (modelId is not null)
        {
            // An explicitly supplied blank model is a configuration bug, not a request to fall
            // back: silently substituting the default would run a Sonnet-tier agent on Haiku and
            // show up only as degraded matrix accuracy.
            ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

            // The per-call override is the other way a floating alias enters the pipeline, and the
            // likelier one: the tiered call sites name their model in code, where 'claude-sonnet-4-6'
            // reads as perfectly reasonable. Checked here as well as in Validate() so neither route
            // is left open.
            if (!AnthropicProviderOptions.IsDatedSnapshot(modelId))
            {
                throw new ArgumentException(
                    AnthropicProviderOptions.DescribeAliasHazard(nameof(modelId), modelId),
                    nameof(modelId));
            }
        }

        if (maxOutputTokens is { } requestedTokens)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestedTokens, 0);
        }

        return _client.AsAIAgent(
            model: modelId ?? _defaultModelId,
            instructions: instructions,
            name: name,
            defaultMaxTokens: maxOutputTokens ?? _defaultMaxOutputTokens);
    }

    /// <summary>Disposes the shared Anthropic client. Agents created by this factory stop working.</summary>
    /// <remarks>
    /// Idempotent and safe to call concurrently with <see cref="CreateAgent"/>: the exchange means
    /// exactly one caller disposes the client. A concurrent <see cref="CreateAgent"/> either
    /// observes the flag and throws <see cref="ObjectDisposedException"/>, or slips through and
    /// builds an agent on a client that is about to close - which is inherent to disposing a shared
    /// resource under load, and is why the factory is a host-lifetime singleton rather than
    /// something callers dispose ad hoc.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _client.Dispose();
    }
}
