using Cbix.Providers.Anthropic;

namespace Cbix.UnitTests.Providers;

/// <summary>
/// Marks the test classes that read or mutate Anthropic environment variables, so xUnit serialises
/// them against each other.
/// </summary>
/// <remarks>
/// Environment variables are process-global: a class that sets one while another is constructing a
/// factory would produce failures that depend on scheduling. Only the classes that actually touch
/// this state join the collection - the containment tests stay parallel, because serialising the
/// whole assembly would slow every unrelated suite to fix a problem they do not have.
/// </remarks>
[CollectionDefinition(AnthropicEnvironmentCollection.Name)]
public sealed class AnthropicEnvironmentCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "Anthropic provider environment (serialised: mutates process state)";
}

/// <summary>
/// Regression tests for the ambient-environment invariants the adapter depends on, and that story
/// S01-17 builds on when it swaps in a real secrets provider.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the SDK's handling of its environment variables is <b>not uniform</b>, and
/// the non-uniformity is invisible from the call site. Measured on the pinned SDK 12.35.1 by
/// capturing the outbound request through an injected handler: an explicit API key suppresses
/// <c>ANTHROPIC_API_KEY</c> and on-disk profile resolution, but does <em>not</em> suppress
/// <c>ANTHROPIC_AUTH_TOKEN</c> (the client sends both credentials and the API rejects the pair) and
/// does <em>not</em> suppress <c>ANTHROPIC_BASE_URL</c> (the request, its key and the document
/// content go to whatever host that variable names). The adapter closes both holes; these tests
/// stop them reopening on an SDK bump.
/// </para>
/// <para>
/// <b>Scope, stated honestly.</b> What is asserted here is everything observable from outside the
/// adapter: the endpoint it resolved, and whether construction fails. The wire-level claim - that
/// <c>X-Api-Key</c> carries the configured key and no <c>Authorization</c> header is sent - was
/// established by the recorded probe, and re-asserting it in-repo would need a test-only
/// <c>HttpMessageHandler</c> seam on the factory. That seam is deliberately absent: exposing the
/// client or its options would hand callers the key-bearing <c>ClientOptions</c> (whose
/// <c>ToString()</c> was measured printing the key in clear), which is a worse trade than the
/// coverage it buys. Re-run the probe on an SDK upgrade.
/// </para>
/// </remarks>
[Collection(AnthropicEnvironmentCollection.Name)]
public sealed class AnthropicAmbientEnvironmentTests
{
    private const string DummyApiKey = "unit-test-placeholder-key-not-a-real-credential";

    [Fact]
    public void ResolvedEndpoint_IsGovernedByConfigurationNotByTheEnvironment()
    {
        // The headline regression. Without an explicit BaseUrl the SDK was measured taking the
        // endpoint from this variable - which would send the API key and the document text to an
        // arbitrary host, with nothing in the logs to show it happened.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_BASE_URL", "https://attacker.example.invalid");

        using AnthropicAgentFactory factory = new(new AnthropicProviderOptions { ApiKey = DummyApiKey });

        Assert.Equal(new Uri(AnthropicProviderOptions.DefaultBaseUrl), factory.ResolvedEndpoint);
    }

    [Fact]
    public void ResolvedEndpoint_HonoursAnExplicitlyConfiguredEndpoint()
    {
        // The other half: pinning the endpoint must not mean hardcoding it. An approved egress
        // proxy (design 8) is a configuration change.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_BASE_URL", "https://attacker.example.invalid");

        AnthropicProviderOptions options = new()
        {
            ApiKey = DummyApiKey,
            BaseUrl = "https://egress-proxy.internal.example",
        };

        using AnthropicAgentFactory factory = new(options);

        Assert.Equal(new Uri("https://egress-proxy.internal.example"), factory.ResolvedEndpoint);
    }

    [Fact]
    public void Construction_FailsClosedWhenAnthropicAuthTokenIsSet()
    {
        // Fails at startup naming the variable, rather than as a 401 on the first real call - which
        // would land mid-run, after checkpoints, on a document that has already paid for other
        // agents' calls.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_AUTH_TOKEN", "an-ambient-oauth-token");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => new AnthropicAgentFactory(new AnthropicProviderOptions { ApiKey = DummyApiKey }));

        Assert.Contains("ANTHROPIC_AUTH_TOKEN", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Construction_SucceedsWhenAnthropicApiKeyIsSetInTheEnvironment()
    {
        // Deliberately NOT rejected. The explicit key was measured to win, and CLAUDE.md sanctions
        // the variable as the local developer channel - failing on it would break the documented
        // workflow for no security gain. This test is what stops someone "hardening" the guard by
        // adding it to the rejected list.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_API_KEY", "an-ambient-developer-key");

        using AnthropicAgentFactory factory = new(new AnthropicProviderOptions { ApiKey = DummyApiKey });

        Assert.Equal(new Uri(AnthropicProviderOptions.DefaultBaseUrl), factory.ResolvedEndpoint);
    }

    [Fact]
    public void Construction_SucceedsWhenOnlyProfileVariablesAreSet()
    {
        // Measured suppressed: supplying an explicit key closes the SDK's on-disk profile
        // resolution path entirely. Recorded as a test so the asymmetry with ANTHROPIC_AUTH_TOKEN
        // stays a measurement rather than folklore.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_PROFILE", "some-profile");
        scope.Set("ANTHROPIC_CONFIG_DIR", Path.Combine(Path.GetTempPath(), "cbix-nonexistent-profile-dir"));

        using AnthropicAgentFactory factory = new(new AnthropicProviderOptions { ApiKey = DummyApiKey });

        Assert.Equal(new Uri(AnthropicProviderOptions.DefaultBaseUrl), factory.ResolvedEndpoint);
    }

    [Theory]
    [InlineData("http://api.anthropic.com")]
    [InlineData("ftp://api.anthropic.com")]
    public void Validate_RejectsANonHttpsEndpoint(string baseUrl)
    {
        AnthropicProviderOptions options = new() { ApiKey = DummyApiKey, BaseUrl = baseUrl };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-absolute-uri")]
    public void Validate_RejectsABlankOrRelativeEndpoint(string baseUrl)
    {
        AnthropicProviderOptions options = new() { ApiKey = DummyApiKey, BaseUrl = baseUrl };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    /// <summary>
    /// Sets environment variables for the duration of a test and restores their prior values.
    /// </summary>
    /// <remarks>
    /// Restore happens in <see cref="Dispose"/>, which the <c>using</c> statement runs even when the
    /// test fails - a test that leaked a hostile <c>ANTHROPIC_BASE_URL</c> would poison every later
    /// test in this collection and be diagnosed as a bug in the wrong place.
    /// </remarks>
    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

        internal void Set(string name, string? value)
        {
            if (!_original.ContainsKey(name))
            {
                _original[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach ((string name, string? value) in _original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            _original.Clear();
        }
    }
}
