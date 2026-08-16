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
public sealed class AnthropicEnvironmentCollection : ICollectionFixture<AnthropicEnvironmentFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "Anthropic provider environment (serialised: mutates process state)";
}

/// <summary>
/// Clears the Anthropic environment variables for the lifetime of the collection, so the tests
/// that read them start from a known state rather than from whatever the developer exported.
/// </summary>
/// <remarks>
/// <para>
/// Without this the suite is not hermetic, and not in a theoretical way: exporting
/// <c>ANTHROPIC_AUTH_TOKEN</c> - the ordinary way to authenticate through a corporate gateway or an
/// OAuth profile on a developer workstation - was measured to fail 18 tests, because the adapter
/// deliberately refuses to start while it is set. A release-gate suite that passes or fails on
/// ambient shell state is reporting on the workstation, not on the code.
/// </para>
/// <para>
/// A collection fixture rather than a per-test scope: it establishes the baseline once, and every
/// class in this collection inherits it, including ones owned by other stories. Per-test scopes
/// still work on top of it - they save and restore against this cleaned baseline, so the tests that
/// deliberately set a variable are unaffected.
/// </para>
/// <para>
/// Mutating process-global state is safe here only because every test in the assembly that touches
/// these variables lives in this one serialised collection. If a future test outside it starts
/// reading them, it belongs in this collection too.
/// </para>
/// </remarks>
public sealed class AnthropicEnvironmentFixture : IDisposable
{
    private static readonly string[] ManagedVariables =
    [
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_PROFILE",
        "ANTHROPIC_CONFIG_DIR",
    ];

    private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);

    /// <summary>Captures and clears the managed variables.</summary>
    public AnthropicEnvironmentFixture()
    {
        foreach (string name in ManagedVariables)
        {
            _original[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>Restores the developer's environment so a test run leaves no trace.</summary>
    public void Dispose()
    {
        foreach ((string name, string? value) in _original)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        _original.Clear();
    }
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
/// <b>Scope.</b> This class covers what is decidable without sending a request: that construction
/// fails closed, and that the options validator rejects a bad endpoint. The claims about what
/// actually goes on the wire - which host, which credential headers - are asserted in
/// <see cref="AnthropicWireTests"/> against a captured request, because they are properties of the
/// SDK's behaviour rather than of any value this adapter assigns to itself.
/// </para>
/// </remarks>
[Collection(AnthropicEnvironmentCollection.Name)]
public sealed class AnthropicAmbientEnvironmentTests
{
    private const string DummyApiKey = "unit-test-placeholder-key-not-a-real-credential";

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
        // adding it to the rejected list; that the explicit key is the one actually sent is
        // asserted at the wire in AnthropicWireTests.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_API_KEY", "an-ambient-developer-key");

        using AnthropicAgentFactory factory = new(new AnthropicProviderOptions { ApiKey = DummyApiKey });

        Assert.NotNull(factory);
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
}
