namespace Cbix.Providers.Anthropic;

/// <summary>
/// Configuration for the Anthropic provider adapter: which endpoint to call, which model, how
/// large a response to allow, and the API key to authenticate with.
/// </summary>
/// <remarks>
/// <para>
/// <b>The key value arrives from outside.</b> This type is a carrier, not a source: it never reads
/// an environment variable, a file, or a configuration provider to populate itself. Story S01-17
/// replaces whoever populates <see cref="ApiKey"/> with the bank-standard secrets manager
/// (design 8), and that swap must not reshape anything here - which is why the property is a plain
/// settable <see cref="string"/> and the type has a public parameterless constructor. Both are what
/// <c>IOptions&lt;T&gt;</c> binding and a secrets provider need; a required constructor parameter
/// would force S01-17 to redesign this type instead of just supplying a value.
/// </para>
/// <para>
/// <b>This is a class, not a record, on purpose.</b> A record synthesises a <c>ToString</c> that
/// prints every property - so the first log line, exception message or debugger snapshot that
/// touched an options instance would exfiltrate the API key. This is not hypothetical: the SDK's
/// own <c>ClientOptions</c> is record-like, and its <c>ToString()</c> was measured printing
/// <c>ApiKey = &lt;the key&gt;</c> in clear text. The same reasoning bars adding a
/// <c>ToString</c> override that includes <see cref="ApiKey"/>, and bars serialising this type
/// wholesale.
/// </para>
/// <para>
/// <b>Ambient environment state (measured on the pinned Anthropic SDK 12.35.1).</b> The SDK reads
/// several environment variables, and it does <em>not</em> treat them uniformly. Each row below was
/// measured by capturing the outbound request through an injected <c>DelegatingHandler</c>, with an
/// explicit <see cref="ApiKey"/> always set:
/// <list type="table">
///   <listheader><term>Variable</term><description>Measured effect, and this adapter's response</description></listheader>
///   <item>
///     <term><c>ANTHROPIC_API_KEY</c></term>
///     <description>
///     Ignored - the request carried only the explicit key. Harmless, and CLAUDE.md sanctions it as
///     a developer channel, so it is deliberately <em>not</em> rejected.
///     </description>
///   </item>
///   <item>
///     <term><c>ANTHROPIC_AUTH_TOKEN</c></term>
///     <description>
///     <b>Not suppressed.</b> The request carried <em>both</em> <c>X-Api-Key</c> (the explicit key)
///     and <c>Authorization: Bearer</c> (the environment token). Anthropic rejects dual credentials,
///     so this surfaces as a 401 on the first real call - mid-run, after checkpoints, on a document
///     that has already paid for other agents. Handled two ways: the adapter sets
///     <c>AuthToken = null</c> explicitly on the client options, which was measured to remove the
///     stray header; and <see cref="Validate"/> additionally refuses to start while the variable is
///     set, because an operator who exported it believes it is in use and silently ignoring it is
///     its own failure.
///     </description>
///   </item>
///   <item>
///     <term><c>ANTHROPIC_BASE_URL</c></term>
///     <description>
///     <b>Not suppressed, and the most serious of the three.</b> With no explicit base URL the
///     request went to the host named by the variable, carrying the API key and the document
///     content. Handled by <see cref="BaseUrl"/>, which is always set explicitly on the client
///     options; the explicit value was measured to win.
///     </description>
///   </item>
///   <item>
///     <term><c>ANTHROPIC_PROFILE</c>, <c>ANTHROPIC_CONFIG_DIR</c></term>
///     <description>
///     Ignored - the request carried only the explicit key. The SDK gates on-disk profile
///     resolution behind "no explicit credential was supplied", and supplying one closes that path,
///     so these need no guard here. Recorded so the asymmetry with <c>ANTHROPIC_AUTH_TOKEN</c> is a
///     documented measurement rather than an assumption someone re-derives later.
///     </description>
///   </item>
/// </list>
/// The general lesson, and the reason the SDK version is pinned exactly: the constructor gate that
/// suppresses profile resolution does <em>not</em> govern the individual option lazies, each of
/// which reads its own environment variable independently. Re-run the probe on any SDK upgrade.
/// </para>
/// </remarks>
public sealed class AnthropicProviderOptions
{
    /// <summary>
    /// Configuration section this type binds from, so the host and any future binding code agree
    /// on one spelling rather than repeating a magic string.
    /// </summary>
    /// <remarks>
    /// The section carries <see cref="BaseUrl"/>, <see cref="ModelId"/> and
    /// <see cref="MaxOutputTokens"/> only. <see cref="ApiKey"/> must never appear in a
    /// configuration file - see the security note on that property, and the test that enforces it.
    /// </remarks>
    public const string SectionName = "Cbix:Providers:Anthropic";

    /// <summary>
    /// The model used when a call site does not name one: the Haiku tier that design 7 assigns to
    /// triage and to six of the seven section agents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <b>dated snapshot</b>, not the <c>claude-haiku-4-5</c> alias, and the distinction
    /// is the whole point. An alias is repointed by the provider: the pipeline would silently begin
    /// calling a different model, invalidating the golden-set accuracy figures that gate release
    /// (CLAUDE.md quality gate) with no change to any file in this repository. Design 11 says to pin
    /// exact model strings; only the dated snapshot actually is one.
    /// </para>
    /// <para>
    /// This is the <em>default</em>, not the only tier. The Matrix agent runs on Sonnet because it
    /// must read the permission table visually; that call site passes its model explicitly to
    /// <see cref="AnthropicAgentFactory.CreateAgent"/>. Which agent sits on which tier is decided by
    /// the eval harness, not by dogma - so the per-agent override is a first-class parameter rather
    /// than a second options type.
    /// </para>
    /// </remarks>
    public const string DefaultModelId = "claude-haiku-4-5-20251001";

    /// <summary>The Anthropic API endpoint. Always set explicitly - never inherited from the environment.</summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    /// <summary>
    /// Default cap on tokens the model may generate per response, applied when a call site does
    /// not override it.
    /// </summary>
    /// <remarks>
    /// The integration's own default is 4096 and this matches it deliberately: stating the value
    /// here makes it ours, so a change in the prerelease package's default cannot silently truncate
    /// a section agent's structured output.
    /// </remarks>
    public const int DefaultMaxOutputTokens = 4096;

    /// <summary>
    /// Gets or sets the Anthropic API key.
    /// </summary>
    /// <remarks>
    /// <b>Never source this from a tracked file.</b> It comes from user-secrets, the environment,
    /// or (from S01-17) the bank's secrets manager - never <c>appsettings*.json</c> and never
    /// <c>launchSettings.json</c>. It must not be logged, included in an exception message, or
    /// written to a checkpoint.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Gets or sets the API endpoint. Defaults to <see cref="DefaultBaseUrl"/>.
    /// </summary>
    /// <remarks>
    /// Configurable because a future deployment may front the API with an approved egress proxy
    /// (design 8 allow-lists egress), and pinned because the alternative is inheriting
    /// <c>ANTHROPIC_BASE_URL</c> - measured to redirect the client, API key and document content to
    /// whatever host that variable names.
    /// </remarks>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>Gets or sets the default model id. Defaults to <see cref="DefaultModelId"/>.</summary>
    public string ModelId { get; set; } = DefaultModelId;

    /// <summary>
    /// Gets or sets the default per-response output token cap. Defaults to
    /// <see cref="DefaultMaxOutputTokens"/>.
    /// </summary>
    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    /// <summary>
    /// Gets or sets a transport for the underlying client. Test seam; <see langword="null"/> in
    /// production, where the SDK supplies its own pooled client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <c>internal</c> and visible only to the unit-test assembly. It exists because
    /// the claims this adapter makes about ambient environment state - that the configured endpoint
    /// governs where requests go, that the configured key is the credential sent, and that no
    /// <c>Authorization</c> header accompanies it - are properties of the <em>outbound request</em>.
    /// Asserting them on fields this class assigns to itself would prove only that the class agrees
    /// with itself; the SDK is the component whose behaviour is in question, and a future
    /// prerelease could ignore <c>ClientOptions.BaseUrl</c> without a single test noticing.
    /// </para>
    /// <para>
    /// Internal rather than public because a transport is not a configuration knob: exposing it
    /// would invite production code to swap the HTTP stack, and a caller holding the transport is
    /// one step from observing the credential headers the adapter exists to keep contained.
    /// </para>
    /// </remarks>
    internal HttpClient? Transport { get; set; }

    /// <summary>
    /// Fails closed on the options, and on hostile ambient environment state, before any agent is
    /// built.
    /// </summary>
    /// <remarks>
    /// Validating here rather than at first use means a misconfigured deployment fails at startup
    /// with a message naming the offending setting, instead of surfacing as an authentication error
    /// partway through a document run that has already paid for other agents' calls and written
    /// checkpoints. That is also why the environment check lives in this method despite making it
    /// impure: S01-17 will call <see cref="Validate"/> during host startup, and the guard has to run
    /// at the same moment to be worth anything.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// An option is absent or out of range, or a conflicting ambient credential is present.
    /// </exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            // The message names the property and the sourcing rule; it must never echo the value,
            // because exception messages reach logs and review UIs.
            throw new InvalidOperationException(
                $"{nameof(AnthropicProviderOptions)}.{nameof(ApiKey)} is not set. Supply it from user-secrets, "
                    + "the environment, or the secrets manager - never from a tracked configuration file.");
        }

        if (string.IsNullOrWhiteSpace(ModelId))
        {
            throw new InvalidOperationException(
                $"{nameof(AnthropicProviderOptions)}.{nameof(ModelId)} is not set. Pin an exact, dated model "
                    + "snapshot; an empty value would let the provider choose, which the design forbids.");
        }

        if (!IsDatedSnapshot(ModelId))
        {
            throw new InvalidOperationException(
                DescribeAliasHazard($"{nameof(AnthropicProviderOptions)}.{nameof(ModelId)}", ModelId));
        }

        if (MaxOutputTokens <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(AnthropicProviderOptions)}.{nameof(MaxOutputTokens)} must be greater than zero; "
                    + $"it is {MaxOutputTokens}.");
        }

        ValidateBaseUrl();
        EnsureNoConflictingAmbientCredential();
    }

    /// <summary>
    /// Resolves <see cref="BaseUrl"/> to an absolute HTTPS <see cref="Uri"/>.
    /// </summary>
    /// <remarks>Call only after <see cref="Validate"/> has succeeded.</remarks>
    internal Uri ResolveBaseUrl() => new(BaseUrl, UriKind.Absolute);

    /// <summary>
    /// Reports whether a model id is a dated snapshot (a trailing <c>-yyyyMMdd</c>) rather than a
    /// floating alias.
    /// </summary>
    /// <remarks>
    /// Shape only - it does not check that the date exists or that the model does. Both are the
    /// provider's business and would fail loudly at the first call; what cannot fail loudly, and is
    /// therefore what this catches, is an alias silently continuing to work while pointing at a
    /// different model than the one the golden set was measured against.
    /// </remarks>
    internal static bool IsDatedSnapshot(string modelId)
    {
        int lastSeparator = modelId.LastIndexOf('-');
        if (lastSeparator < 0)
        {
            return false;
        }

        ReadOnlySpan<char> suffix = modelId.AsSpan(lastSeparator + 1);

        return suffix.Length == 8 && !suffix.ContainsAnyExceptInRange('0', '9');
    }

    /// <summary>Builds the message explaining why a floating alias is refused.</summary>
    internal static string DescribeAliasHazard(string subject, string modelId) =>
        $"{subject} ('{modelId}') is not a dated model snapshot. Anthropic repoints aliases such as "
            + "'claude-haiku-4-5' to newer models, so an alias would silently change what the pipeline "
            + "calls - invalidating the golden-set accuracy figures that gate release, with no change to "
            + "any file in this repository. Pin the dated form (for example "
            + $"'{DefaultModelId}').";

    /// <summary>Fails closed on the endpoint: absolute HTTPS only.</summary>
    /// <remarks>
    /// HTTPS is not negotiable - the request carries the API key in a header and the document
    /// content in its body, and design 8's whole premise is that this traffic leaves the bank's
    /// network over TLS. A plaintext endpoint would put both on the wire in clear.
    /// </remarks>
    private void ValidateBaseUrl()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            throw new InvalidOperationException(
                $"{nameof(AnthropicProviderOptions)}.{nameof(BaseUrl)} is not set. It must be pinned explicitly: "
                    + "an unset endpoint lets the ANTHROPIC_BASE_URL environment variable redirect the API key "
                    + "and document content to an arbitrary host.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? parsed))
        {
            throw new InvalidOperationException(
                $"{nameof(AnthropicProviderOptions)}.{nameof(BaseUrl)} ('{BaseUrl}') is not an absolute URI.");
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{nameof(AnthropicProviderOptions)}.{nameof(BaseUrl)} must use HTTPS; '{parsed.Scheme}' would put "
                    + "the API key and the document content on the wire in clear text.");
        }
    }

    /// <summary>
    /// Refuses to start while <c>ANTHROPIC_AUTH_TOKEN</c> is set in the environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on SDK 12.35.1: an explicit API key does <em>not</em> suppress this variable. The
    /// client sends <c>X-Api-Key</c> and <c>Authorization: Bearer</c> together, and Anthropic
    /// rejects requests carrying two credentials - so the symptom is a 401 on the first real call,
    /// far from the cause.
    /// </para>
    /// <para>
    /// The adapter also neutralises the header directly (see <see cref="AnthropicAgentFactory"/>),
    /// so this check is not what makes the pipeline correct - it is what makes the operator's
    /// intent explicit. Someone who exported <c>ANTHROPIC_AUTH_TOKEN</c> expects it to be used;
    /// quietly discarding it is a different failure from the 401, not an absence of failure.
    /// </para>
    /// <para>
    /// Deliberately narrow: <c>ANTHROPIC_API_KEY</c> is <em>not</em> rejected. It was measured to be
    /// genuinely overridden by the explicit key, and CLAUDE.md sanctions it as a developer channel -
    /// failing on it would break the documented local workflow for no security gain.
    /// </para>
    /// </remarks>
    private static void EnsureNoConflictingAmbientCredential()
    {
        const string ConflictingVariable = "ANTHROPIC_AUTH_TOKEN";

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ConflictingVariable)))
        {
            throw new InvalidOperationException(
                $"The '{ConflictingVariable}' environment variable is set. The Anthropic SDK sends it as an "
                    + "'Authorization: Bearer' header alongside the configured API key, and the API rejects "
                    + "requests carrying two credentials - which would fail mid-run rather than at startup. "
                    + "Unset it; the API key comes from the configured secret, not from the environment.");
        }
    }
}
