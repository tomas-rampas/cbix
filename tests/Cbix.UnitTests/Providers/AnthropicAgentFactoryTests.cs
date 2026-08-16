using Cbix.Providers.Anthropic;

using Microsoft.Agents.AI;

namespace Cbix.UnitTests.Providers;

/// <summary>
/// Construction-only tests for the Anthropic provider adapter (story S01-08).
/// <para>
/// Every test here runs offline. That is not merely convenient - it is an assertion about the
/// adapter: building an <see cref="AIAgent"/> must be a pure, in-process operation, so a fictional
/// key is sufficient and CI never reaches Anthropic. If that ever stops being true, these tests
/// fail (or hang) rather than silently spending money on a green build.
/// </para>
/// <para>
/// Offline-ness is proved through <see cref="AnthropicProviderOptions.BaseUrl"/> rather than by
/// setting <c>ANTHROPIC_BASE_URL</c>: pointing the configured endpoint at an unroutable host is an
/// explicit seam, and a test that depended on ambient environment state to prove the adapter
/// ignores ambient environment state would be arguing with itself.
/// </para>
/// <para>
/// In the serialised environment collection because <see cref="AnthropicProviderOptions.Validate"/>
/// reads <c>ANTHROPIC_AUTH_TOKEN</c>; without that, a sibling class mutating it would fail these
/// tests at random.
/// </para>
/// </summary>
[Collection(AnthropicEnvironmentCollection.Name)]
public sealed class AnthropicAgentFactoryTests
{
    /// <summary>
    /// Self-labelling placeholder. Deliberately carries no <c>sk-ant-</c> vendor prefix: secret
    /// scanners pattern-match that shape, and a repository that trips its own scanner on test data
    /// teaches everyone to ignore the alerts.
    /// </summary>
    private const string DummyApiKey = "unit-test-placeholder-key-not-a-real-credential";

    /// <summary>Reserved, unroutable per RFC 5737 - a request would fail rather than reach anything.</summary>
    private const string UnroutableEndpoint = "https://192.0.2.1";

    private static AnthropicProviderOptions ValidOptions() => new()
    {
        ApiKey = DummyApiKey,
        BaseUrl = UnroutableEndpoint,
    };

    [Fact]
    public void CreateAgent_ReturnsAnAiAgentWithoutTouchingTheNetwork()
    {
        // The endpoint is unroutable, so if construction or agent-building performed any I/O this
        // would fail or stall rather than pass.
        using AnthropicAgentFactory factory = new(ValidOptions());

        AIAgent agent = factory.CreateAgent("docControl", "Extract, never interpret.");

        Assert.NotNull(agent);
    }

    [Fact]
    public void CreateAgent_DeclaredReturnTypeIsTheFrameworkAbstraction()
    {
        // The story's acceptance criterion is about the STATIC type. A runtime `is AIAgent` check
        // would also pass if the method were declared to return the integration's concrete agent
        // class - and every caller would then be coupled to the provider.
        Type returnType = typeof(AnthropicAgentFactory)
            .GetMethod(nameof(AnthropicAgentFactory.CreateAgent))!
            .ReturnType;

        Assert.Equal(typeof(AIAgent), returnType);
    }

    [Fact]
    public void CreateAgent_HonoursThePerCallModelOverride()
    {
        // Design 7 tiers agents across models (Matrix on Sonnet, the rest on Haiku), so the
        // override must be a real parameter rather than a second options object.
        using AnthropicAgentFactory factory = new(ValidOptions());

        AIAgent agent = factory.CreateAgent(
            "matrix",
            "Emit exactly products x categories cells.",
            "claude-sonnet-4-6-20260122");

        Assert.NotNull(agent);
    }

    [Fact]
    public void ResolvedEndpoint_ReportsTheConfiguredEndpoint()
    {
        // Design 8 auditability: "which endpoint did this run talk to" is part of tracing a
        // published value back to what produced it.
        using AnthropicAgentFactory factory = new(ValidOptions());

        Assert.Equal(new Uri(UnroutableEndpoint), factory.ResolvedEndpoint);
    }

    [Fact]
    public void DefaultModelId_IsADatedSnapshotNotAFloatingAlias()
    {
        // Design 11: pin exact model strings. The `claude-haiku-4-5` alias is repointed by the
        // provider, so the pipeline would silently start calling a different model and invalidate
        // the golden-set accuracy figures that gate release - with no change to any file here.
        // Only a dated snapshot is actually an exact pin.
        Assert.Equal("claude-haiku-4-5-20251001", AnthropicProviderOptions.DefaultModelId);
        Assert.Equal(AnthropicProviderOptions.DefaultModelId, new AnthropicProviderOptions().ModelId);

        // The distinguishing property, asserted rather than assumed: a dated snapshot carries a
        // trailing 8-digit date that a bare alias does not.
        string[] segments = AnthropicProviderOptions.DefaultModelId.Split('-');
        Assert.True(
            segments[^1].Length == 8 && segments[^1].All(char.IsAsciiDigit),
            $"'{AnthropicProviderOptions.DefaultModelId}' does not end in a yyyyMMdd snapshot date, so it is an "
                + "alias the provider can repoint underneath us.");
    }

    [Fact]
    public void DefaultBaseUrl_IsTheAnthropicApiOverHttps()
    {
        Assert.Equal("https://api.anthropic.com", AnthropicProviderOptions.DefaultBaseUrl);
        Assert.Equal(AnthropicProviderOptions.DefaultBaseUrl, new AnthropicProviderOptions().BaseUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsAMissingApiKey(string? apiKey)
    {
        AnthropicProviderOptions options = ValidOptions();
        options.ApiKey = apiKey;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new AnthropicAgentFactory(options));

        // Failing at construction rather than at first call means a misconfigured deployment dies
        // at startup, not partway through a run that has already paid for other agents' calls.
        Assert.Contains(nameof(AnthropicProviderOptions.ApiKey), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ErrorMessageNeverEchoesTheKey()
    {
        // Exception messages reach logs and the review UI. A validator that quotes the offending
        // value is the classic way a secret escapes - so the one case where a key IS present but
        // another option is wrong must still not print it.
        AnthropicProviderOptions options = ValidOptions();
        options.ModelId = "   ";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => new AnthropicAgentFactory(options));

        Assert.DoesNotContain(DummyApiKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsANonPositiveOutputTokenCap(int maxOutputTokens)
    {
        AnthropicProviderOptions options = ValidOptions();
        options.MaxOutputTokens = maxOutputTokens;

        Assert.Throws<InvalidOperationException>(() => new AnthropicAgentFactory(options));
    }

    [Fact]
    public void Constructor_RejectsNullOptions() =>
        Assert.Throws<ArgumentNullException>(() => new AnthropicAgentFactory(null!));

    [Theory]
    [InlineData(null, "instructions")]
    [InlineData("", "instructions")]
    [InlineData("   ", "instructions")]
    [InlineData("docControl", null)]
    [InlineData("docControl", "")]
    [InlineData("docControl", "   ")]
    public void CreateAgent_RejectsABlankNameOrInstructions(string? name, string? instructions)
    {
        using AnthropicAgentFactory factory = new(ValidOptions());

        Assert.ThrowsAny<ArgumentException>(() => factory.CreateAgent(name!, instructions!));
    }

    [Fact]
    public void CreateAgent_RejectsABlankExplicitModelRatherThanFallingBack()
    {
        // Silently substituting the default here would run a Sonnet-tier agent on Haiku and show
        // up only as degraded matrix-cell accuracy several stories later.
        using AnthropicAgentFactory factory = new(ValidOptions());

        Assert.ThrowsAny<ArgumentException>(() => factory.CreateAgent("matrix", "Read the table.", "   "));
    }

    [Fact]
    public void CreateAgent_RejectsANonPositiveOutputTokenOverride()
    {
        using AnthropicAgentFactory factory = new(ValidOptions());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => factory.CreateAgent("docControl", "Extract, never interpret.", maxOutputTokens: 0));
    }

    [Fact]
    public void CreateAgent_AfterDisposeThrowsRatherThanReturningADeadAgent()
    {
        AnthropicAgentFactory factory = new(ValidOptions());
        factory.Dispose();

        // An agent built on a disposed client would fail at its first call - deep inside a workflow
        // superstep, long after the mistake, and after a checkpoint had been written.
        Assert.Throws<ObjectDisposedException>(() => factory.CreateAgent("docControl", "Extract, never interpret."));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        AnthropicAgentFactory factory = new(ValidOptions());

        factory.Dispose();
        factory.Dispose();
    }

    [Fact]
    public void Dispose_IsSafeUnderConcurrentCreateAgent()
    {
        // The seven-agent parallel fan-out makes CreateAgent racing host shutdown an ordinary
        // occurrence. Every outcome must be either a valid agent or ObjectDisposedException -
        // never a torn state or a NullReferenceException from a half-disposed client.
        AnthropicAgentFactory factory = new(ValidOptions());

        using Barrier barrier = new(8);
        List<Exception> unexpected = [];
        List<Thread> threads = [];

        for (int index = 0; index < 7; index++)
        {
            Thread thread = new(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    factory.CreateAgent("section", "Extract, never interpret.");
                }
                catch (ObjectDisposedException)
                {
                    // The expected race outcome.
                }
                catch (Exception error)
                {
                    lock (unexpected)
                    {
                        unexpected.Add(error);
                    }
                }
            });

            threads.Add(thread);
            thread.Start();
        }

        barrier.SignalAndWait();
        factory.Dispose();

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.True(
            unexpected.Count == 0,
            $"Concurrent CreateAgent during Dispose produced unexpected failures: "
                + string.Join(", ", unexpected.Select(error => error.GetType().Name)));
    }
}
