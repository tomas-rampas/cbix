using Cbix.Core.Secrets;

using Cbix.Providers.Anthropic;

using Cbix.UnitTests.Providers;

using Cbix.Worker;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cbix.UnitTests.Hosting;

/// <summary>
/// Composition tests for the worker's service graph (story S01-17).
/// <para>
/// Until now nothing exercised the DI graph at all: every type was constructed directly by a test,
/// so a registration that was missing, mis-scoped, or never disposed would have shipped green. These
/// build the real graph through the same entry point <c>Program.cs</c> uses, which is what makes the
/// composition root a tested artefact rather than three lines nobody runs until deployment.
/// </para>
/// <para>
/// Most tests compose over an <em>empty</em> application builder. The default one reads the ambient
/// environment and the test host's own <c>appsettings.json</c>, so a test about which source
/// supplied a secret would be answering a question the machine had already answered for it - and it
/// would pass or fail depending on whether the developer running it had exported
/// <c>ANTHROPIC_API_KEY</c>. The single deliberate exception is
/// <see cref="DefaultHostConfiguration_LoadsThePinnedProviderShape"/>, which exists precisely to pin
/// what the default builder does; see its remarks.
/// </para>
/// <para>
/// In the serialised environment collection because composition calls
/// <see cref="AnthropicProviderOptions.Validate"/>, which reads <c>ANTHROPIC_AUTH_TOKEN</c>, and
/// because two tests register the real environment-variables provider.
/// </para>
/// </summary>
[Collection(AnthropicEnvironmentCollection.Name)]
public sealed class WorkerCompositionTests : IDisposable
{
    /// <summary>
    /// Self-labelling placeholder. Deliberately carries no <c>sk-ant-</c> vendor prefix: secret
    /// scanners pattern-match that shape, and a repository that trips its own scanner on test data
    /// teaches everyone to ignore the alerts.
    /// </summary>
    private const string PlaceholderApiKey = "composition-test-placeholder-not-a-real-credential";

    /// <summary>Reserved, unroutable per RFC 5737 - a request would fail rather than reach anything.</summary>
    private const string UnroutableEndpoint = "https://192.0.2.1";

    private const string ScopedApiKeyKey = "Cbix:Providers:Anthropic:ApiKey";

    /// <summary>The environment-variable spelling of <see cref="ScopedApiKeyKey"/>.</summary>
    private const string ScopedApiKeyVariable = "Cbix__Providers__Anthropic__ApiKey";

    private readonly List<string> _temporaryDirectories = [];

    [Fact]
    public void AddCbixWorker_RegistersTheAgentFactoryAsASingleton()
    {
        using IHost host = Compose(SecretsManagerSource(PlaceholderApiKey));

        AnthropicAgentFactory factory = host.Services.GetRequiredService<AnthropicAgentFactory>();

        // Singleton, not transient: the factory owns one Anthropic client whose HTTP handler pools
        // connections across the seven-agent fan-out, and a per-resolution client would also leak.
        Assert.Same(factory, host.Services.GetRequiredService<AnthropicAgentFactory>());
    }

    [Fact]
    public void AddCbixWorker_RegistersTheBackgroundService()
    {
        using IHost host = Compose(SecretsManagerSource(PlaceholderApiKey));

        // By name: the hosted service is internal to Cbix.Worker, which is correct - nothing outside
        // that assembly constructs it - and naming it here would force it public for a test's sake.
        Assert.Contains(
            host.Services.GetServices<IHostedService>(),
            service => string.Equals(service.GetType().Name, "Worker", StringComparison.Ordinal));
    }

    [Fact]
    public void AddCbixWorker_RegistersTheSecretResolverPort()
    {
        using IHost host = Compose(SecretsManagerSource(PlaceholderApiKey));

        ISecretResolver resolver = host.Services.GetRequiredService<ISecretResolver>();

        // Registered so later stories (the SQL connection string, design 6) resolve credentials
        // through the same port rather than each inventing its own channel.
        Assert.Equal(2, resolver.SourceDescriptions.Count);
        Assert.Contains(ScopedApiKeyKey, resolver.SourceDescriptions[0], StringComparison.Ordinal);
        Assert.Contains("ANTHROPIC_API_KEY", resolver.SourceDescriptions[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_RegistersTheClock()
    {
        using IHost host = Compose(SecretsManagerSource(PlaceholderApiKey));

        Assert.Same(TimeProvider.System, host.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void SecretResolver_AnswersTheVendorAliasOnlyForTheAnthropicKey()
    {
        // The credential-crossover regression, and the reason SecretSource insists an alias source
        // check the requested name. The registered resolver is a container-wide service; written as
        // `_ => ReadTheVariable()` its ANTHROPIC_API_KEY source answered EVERY secret name with the
        // Anthropic key - so the first component to ask for a database connection string would have
        // received an API key and sent it to the database host.
        using IHost host = Compose(VendorEnvironmentAliasSource(PlaceholderApiKey));

        ISecretResolver resolver = host.Services.GetRequiredService<ISecretResolver>();

        Assert.Equal(PlaceholderApiKey, resolver.TryResolve(CbixWorkerHostExtensions.AnthropicApiKeySecretName));
        Assert.Null(resolver.TryResolve("Cbix:Database:ConnectionString"));

        SecretNotFoundException error = Assert.Throws<SecretNotFoundException>(
            () => resolver.Require("Cbix:Database:ConnectionString"));

        Assert.Equal("Cbix:Database:ConnectionString", error.SecretName);
        Assert.DoesNotContain(PlaceholderApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_FailsAtStartupWhenNoSourceCarriesTheKey()
    {
        // The headline behaviour. Without this the run reaches the first model call and fails with a
        // 401 - inside a workflow superstep, after checkpoints, on a document that has already paid
        // for other agents' calls, and reported as "unauthorized" rather than "nobody configured
        // this".
        SecretNotFoundException error = Assert.Throws<SecretNotFoundException>(
            () => Compose(builder => builder.AddInMemoryCollection([])).Dispose());

        Assert.Contains(ScopedApiKeyKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("ANTHROPIC_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Contains("user-secrets", error.Message, StringComparison.Ordinal);

        // The forbidden channels are named so an operator does not "fix" the failure by choosing
        // one of them.
        Assert.Contains("command-line argument", error.Message, StringComparison.Ordinal);
        Assert.Contains("settings.json", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_AcceptsTheVendorEnvironmentVariableWhenTheScopedKeyIsAbsent()
    {
        // The documented developer channel (CLAUDE.md), reached through configuration because the
        // host registers the process environment as a configuration source.
        using IHost host = Compose(VendorEnvironmentAliasSource(PlaceholderApiKey));

        Assert.NotNull(host.Services.GetRequiredService<AnthropicAgentFactory>());
    }

    [Fact]
    public void AddCbixWorker_PrefersTheScopedConfigurationKeyOverTheVendorEnvironmentVariable()
    {
        // Precedence, proved differentially because the composed graph exposes no way to read the
        // resolved key back. The scoped key is planted in a file provider, which the allowlist
        // refuses on sight; the vendor variable holds a perfectly usable value. If the vendor
        // variable were consulted first, composition would succeed and never look at the file. It
        // throwing is what proves the scoped key was consulted first.
        Assert.Throws<SecretGovernanceException>(
            () => Compose(
                TrackedConfigurationFile("appsettings.json", ScopedKeyJson(PlaceholderApiKey)),
                VendorEnvironmentAliasSource(PlaceholderApiKey)).Dispose());
    }

    [Fact]
    public void AddCbixWorker_RefusesAKeySuppliedByATrackedConfigurationFile()
    {
        // The run-time half of "no tracked file carries the key". The static asset scans cover this
        // repository; this covers an operator editing a shipped appsettings.json on a server, or an
        // image build that bakes one in - neither of which passes through the repository at all.
        SecretGovernanceException error = Assert.Throws<SecretGovernanceException>(
            () => Compose(TrackedConfigurationFile("appsettings.json", ScopedKeyJson(PlaceholderApiKey))).Dispose());

        Assert.Equal(ScopedApiKeyKey, error.SecretName);
        Assert.Contains("appsettings.json", error.Message, StringComparison.Ordinal);
        Assert.Contains("JsonConfigurationProvider", error.Message, StringComparison.Ordinal);

        // The description names the provider type and the file, never the value.
        Assert.DoesNotContain(PlaceholderApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_RefusesAKeyFromTheApplicationSettingsFileConvention()
    {
        // The measurement that forced the guard's shape to be inverted. Host.CreateApplicationBuilder
        // also loads {ApplicationName}.settings.json - Cbix.Worker.settings.json - at HIGHER
        // precedence than appsettings.json. A denylist of file-name fragments did not know that
        // convention existed, so a key planted here passed the guard end to end.
        SecretGovernanceException error = Assert.Throws<SecretGovernanceException>(
            () => Compose(
                TrackedConfigurationFile("Cbix.Worker.settings.json", ScopedKeyJson(PlaceholderApiKey))).Dispose());

        Assert.Contains("Cbix.Worker.settings.json", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_RefusesATrackedFileKeyEvenWhenAnEnvironmentVariableShadowsIt()
    {
        // The second measurement that forced the shape change. The old guard returned at the first
        // provider holding the key, walking backwards - so a credential committed to appsettings.json
        // became invisible the moment an environment variable overrode it, which is the normal state
        // of a correctly configured deployment. The credential is still committed, still in every
        // build output, still in the image; the guard just never looked.
        using EnvironmentVariableScope scope = new();
        scope.Set(ScopedApiKeyVariable, PlaceholderApiKey);

        SecretGovernanceException error = Assert.Throws<SecretGovernanceException>(
            () => Compose(
                TrackedConfigurationFile("appsettings.json", ScopedKeyJson(PlaceholderApiKey)),
                builder => builder.AddEnvironmentVariables()).Dispose());

        Assert.Contains("appsettings.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_RefusesTheAliasNameInATrackedFileEvenWhenTheScopedKeyIsApproved()
    {
        // Regression for a governance sweep that short-circuited ACROSS sources. LayeredSecretResolver
        // returns at the first source that answers, so once the scoped key resolved from an approved
        // provider the alias source never ran - and its sweep of the ANTHROPIC_API_KEY name never
        // happened. Measured before the fix: this exact configuration composed cleanly and the host
        // STARTED (exit 124 on a timeout, zero refusals) with a credential sitting in appsettings.json.
        //
        // That is not a contrived shape. It is a correctly configured deployment that also happens to
        // have a key committed - the case the run-time guard exists for, because no static scan of
        // this repository can see an operator-edited shipped config or a baked image layer.
        SecretGovernanceException error = Assert.Throws<SecretGovernanceException>(
            () => Compose(
                TrackedConfigurationFile(
                    "appsettings.json",
                    $$"""{ "ANTHROPIC_API_KEY": "{{PlaceholderApiKey}}" } """),
                SecretsManagerSource(PlaceholderApiKey)).Dispose());

        Assert.Contains("appsettings.json", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_SweepsTheAliasNameEvenWhenTheScopedKeyResolvesFirst()
    {
        // The same defect stated as a property rather than a scenario: the sweep must be unconditional
        // per resolution of the Anthropic key, not contingent on which source ends up answering. Here
        // the alias sits on the command line - a channel that is refused outright - while the scoped
        // key resolves from an approved source, so a passing composition would prove the sweep was
        // skipped.
        SecretGovernanceException error = Assert.Throws<SecretGovernanceException>(
            () => Compose(
                builder => builder.AddCommandLine([$"--ANTHROPIC_API_KEY={PlaceholderApiKey}"]),
                SecretsManagerSource(PlaceholderApiKey)).Dispose());

        Assert.Contains("CommandLineConfigurationProvider", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_RefusesAKeySuppliedOnTheCommandLine()
    {
        // The command-line provider is registered last and outranks everything, and process
        // arguments are world-readable in every process listing, shell history and container
        // inspection output. Supplying the secret "correctly" here still discloses it.
        SecretGovernanceException error = Assert.Throws<SecretGovernanceException>(
            () => Compose(
                builder => builder.AddCommandLine([$"--{ScopedApiKeyKey}={PlaceholderApiKey}"])).Dispose());

        Assert.Contains("CommandLineConfigurationProvider", error.Message, StringComparison.Ordinal);
        Assert.Contains("process listing", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_RefusesAKeyFromAnUnrecognisedProviderType()
    {
        // Fail closed. A wrapper such as ChainedConfigurationProvider reports nothing about what is
        // underneath it, and was measured laundering a tracked-file key past a description-based
        // check. Refusing by default makes adding a provider a deliberate act - the Vault/CyberArk
        // provider gets allowlisted by the story that introduces it, with a reason.
        IConfigurationRoot inner = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ScopedApiKeyKey] = PlaceholderApiKey })
            .Build();

        SecretGovernanceException error = Assert.Throws<SecretGovernanceException>(
            () => Compose(builder => builder.AddConfiguration(inner)).Dispose());

        Assert.Contains("ChainedConfigurationProvider", error.Message, StringComparison.Ordinal);
        Assert.Contains("not on the approved list", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderApiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_AcceptsAKeyFromTheUserSecretsStore()
    {
        // The allowlist's positive control: it must not degenerate into "refuse every file". The
        // user-secrets store is a JSON file provider like any other, and it is approved because the
        // file lives in the developer's profile - outside the repository, every build output and
        // every image layer - so it cannot be committed or shipped by accident.
        using IHost host = Compose(TrackedConfigurationFile("secrets.json", ScopedKeyJson(PlaceholderApiKey)));

        Assert.NotNull(host.Services.GetRequiredService<AnthropicAgentFactory>());
    }

    [Fact]
    public void AddCbixWorker_AcceptsNonSecretSettingsFromATrackedConfigurationFile()
    {
        // The refusal is about credentials, not about configuration files. Endpoint and model are
        // exactly what appsettings.json is for, and a guard that blocked them would push operators
        // toward putting everything in environment variables.
        using IHost host = Compose(
            TrackedConfigurationFile(
                "appsettings.json",
                $$"""
                {
                  "Cbix": {
                    "Providers": {
                      "Anthropic": { "BaseUrl": "{{UnroutableEndpoint}}", "MaxOutputTokens": "2048" }
                    }
                  }
                }
                """),
            SecretsManagerSource(PlaceholderApiKey));

        AnthropicAgentFactory factory = host.Services.GetRequiredService<AnthropicAgentFactory>();

        // Design 8 auditability: which endpoint a run talked to is part of tracing a published value
        // back to what produced it - and it is the one setting observable without a network call.
        Assert.Equal(new Uri(UnroutableEndpoint), factory.ResolvedEndpoint);
    }

    [Fact]
    public void AddCbixWorker_RejectsANonNumericOutputTokenCap()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Compose(
                builder => builder.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        [ScopedApiKeyKey] = PlaceholderApiKey,
                        ["Cbix:Providers:Anthropic:MaxOutputTokens"] = "lots",
                    })).Dispose());

        Assert.Contains("MaxOutputTokens", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("an-ambient-oauth-token")]
    [InlineData(" ")]
    public void AddCbixWorker_FailsAtStartupWhenAConflictingAmbientCredentialIsSet(string token)
    {
        // Proves the adapter's ambient-environment guard runs during composition rather than at
        // first use. ANTHROPIC_AUTH_TOKEN makes the SDK send Authorization: Bearer alongside
        // X-Api-Key, and the API rejects the pair - a 401 that would otherwise land mid-run.
        //
        // The whitespace case pins a deliberate asymmetry that looks like an inconsistency. The
        // guard uses IsNullOrEmpty while every sibling check in AnthropicProviderOptions uses
        // IsNullOrWhiteSpace - because this predicate has the opposite polarity. The siblings ask
        // "is a REQUIRED value missing"; this asks "is a FORBIDDEN value PRESENT", and a variable
        // exported as a single space IS present. Measured: !IsNullOrWhiteSpace(" ") is false, so
        // "aligning" the two predicates lets " " slip the guard. This case fails if anyone does.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_AUTH_TOKEN", token);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Compose(SecretsManagerSource(PlaceholderApiKey)).Dispose());

        Assert.Contains("ANTHROPIC_AUTH_TOKEN", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddCbixWorker_RecordsTheResolvedEndpointAtStartup()
    {
        // A configured BaseUrl is a blessed redirect (design 8 anticipates an approved egress
        // proxy), but before this event it was a SILENT one: the API key and the document content
        // would go to another host with nothing anywhere to say so. The event is what makes it
        // observable.
        CapturingLoggerProvider capture = new();

        using IHost host = Compose(
            capture,
            TrackedConfigurationFile(
                "appsettings.json",
                $$"""{ "Cbix": { "Providers": { "Anthropic": { "BaseUrl": "{{UnroutableEndpoint}}" } } } }"""),
            SecretsManagerSource(PlaceholderApiKey));

        // Resolving the hosted services is what a starting host does, and the registration
        // deliberately touches the factory there - so this asserts the event is a STARTUP event and
        // not one that fires whenever something first needs an agent.
        _ = host.Services.GetServices<IHostedService>().ToList();

        Assert.Contains(
            capture.Messages,
            message => message.Contains(UnroutableEndpoint, StringComparison.Ordinal));

        // Whatever else the run logs, the credential is not in it.
        Assert.DoesNotContain(
            capture.Messages,
            message => message.Contains(PlaceholderApiKey, StringComparison.Ordinal));
    }

    [Fact]
    public void HostDisposal_DisposesTheAgentFactory()
    {
        // The reason the factory is registered through a lambda rather than as a pre-built instance:
        // the container disposes what it creates. A leaked HTTP client per host is invisible in a
        // worker that runs once and fatal in a test suite or a restarting service.
        AnthropicAgentFactory factory;

        using (IHost host = Compose(SecretsManagerSource(PlaceholderApiKey)))
        {
            factory = host.Services.GetRequiredService<AnthropicAgentFactory>();
            Assert.NotNull(factory.CreateAgent("docControl", "Extract, never interpret."));
        }

        Assert.Throws<ObjectDisposedException>(
            () => factory.CreateAgent("docControl", "Extract, never interpret."));
    }

    [Fact]
    public void DefaultHostConfiguration_LoadsThePinnedProviderShape()
    {
        // The one deliberately non-hermetic test here, and the one that would have caught the
        // {ApplicationName}.settings.json hole on its own. Every other test states its own
        // configuration sources, which is what makes them deterministic - and is exactly why none of
        // them could see that the REAL host loads a file convention the guard had never heard of.
        //
        // What is pinned is the provider sequence Host.CreateApplicationBuilder produces, because
        // the runtime guard reasons about that sequence. If a future SDK adds, removes or reorders a
        // provider, this fails and someone re-reads the allowlist - instead of the convention
        // drifting unobserved.
        //
        // Content root is a controlled empty directory so no appsettings.json on disk is read, and
        // the environment is pinned to Production so the result does not depend on the developer's
        // DOTNET_ENVIRONMENT. In Development the host additionally registers the user-secrets JSON
        // provider, which the allowlist approves by file name.
        string contentRoot = CreateTemporaryDirectory();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Cbix.Worker",
            ContentRootPath = contentRoot,
            EnvironmentName = Environments.Production,
            Args = [],
        });

        List<string> providers =
        [
            .. ((IConfigurationRoot)builder.Configuration).Providers.Select(DescribeProvider),
        ];

        // Measured on the pinned SDK (net10.0, Microsoft.Extensions.Hosting 10.0.10). The third and
        // fourth entries are the finding: the host loads {ApplicationName}.settings.json and its
        // environment-specific sibling, ABOVE appsettings.json. Nothing in this repository asked for
        // those files.
        Assert.Equal(
            [
                "MemoryConfigurationProvider",
                "EnvironmentVariablesConfigurationProvider",
                "MemoryConfigurationProvider",
                "JsonConfigurationProvider:appsettings.json",
                "JsonConfigurationProvider:appsettings.Production.json",
                "JsonConfigurationProvider:Cbix.Worker.settings.json",
                "JsonConfigurationProvider:Cbix.Worker.settings.Production.json",
                "EnvironmentVariablesConfigurationProvider",
            ],
            providers);

        // The guard walks root.Providers as a flat list. A wrapper in the default shape would mean
        // it was reasoning about a facade rather than about the real sources.
        Assert.DoesNotContain("Chained", string.Join(",", providers), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultHostConfiguration_PutsTheCommandLineProviderLast()
    {
        // Pins the premise of the command-line refusal: the provider is appended after everything
        // else, so a credential passed as an argument outranks user-secrets, the environment and
        // every file - while also being world-readable. It is absent from the shape pinned above
        // only because that builder is given no arguments.
        string contentRoot = CreateTemporaryDirectory();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Cbix.Worker",
            ContentRootPath = contentRoot,
            EnvironmentName = Environments.Production,
            Args = ["--Cbix:Providers:Anthropic:ModelId=not-a-secret"],
        });

        List<string> providers =
        [
            .. ((IConfigurationRoot)builder.Configuration).Providers.Select(DescribeProvider),
        ];

        Assert.Equal("CommandLineConfigurationProvider", providers[^1]);
    }

    [Fact]
    public void AddCbixWorker_RejectsANullBuilder() =>
        Assert.Throws<ArgumentNullException>(() => CbixWorkerHostExtensions.AddCbixWorker(null!));

    [Fact]
    public void AnthropicApiKeySecretName_IsDerivedFromTheProviderSection()
    {
        // Derived, not spelled out: a secret whose name drifted from the section the rest of the
        // provider settings bind from would send operators to a key nothing reads.
        Assert.Equal(
            AnthropicProviderOptions.SectionName + ":ApiKey",
            CbixWorkerHostExtensions.AnthropicApiKeySecretName);
    }

    /// <summary>Removes any temporary configuration files the tests wrote.</summary>
    public void Dispose()
    {
        foreach (string directory in _temporaryDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a test over; the OS reclaims it.
            }
        }

        _temporaryDirectories.Clear();
    }

    /// <summary>A credential-free label for a configuration provider: type name, plus file for file providers.</summary>
    private static string DescribeProvider(IConfigurationProvider provider) => provider switch
    {
        FileConfigurationProvider file => $"{file.GetType().Name}:{file.Source.Path}",
        _ => provider.GetType().Name,
    };

    private static string ScopedKeyJson(string apiKey) =>
        $$"""{ "Cbix": { "Providers": { "Anthropic": { "ApiKey": "{{apiKey}}" } } } }""";

    /// <summary>The source standing in for user-secrets, CI environment variables, or the bank's vault.</summary>
    private static Action<IConfigurationBuilder> SecretsManagerSource(string apiKey) =>
        builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?> { [ScopedApiKeyKey] = apiKey });

    /// <summary>The vendor-wide <c>ANTHROPIC_API_KEY</c> channel, as configuration sees it.</summary>
    private static Action<IConfigurationBuilder> VendorEnvironmentAliasSource(string apiKey) =>
        builder => builder.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = apiKey });

    /// <summary>Builds the graph through the same entry point <c>Program.cs</c> uses.</summary>
    private static IHost Compose(params Action<IConfigurationBuilder>[] sources) =>
        Compose(loggerProvider: null, sources);

    /// <summary>Builds the graph, optionally capturing what it logs.</summary>
    private static IHost Compose(ILoggerProvider? loggerProvider, params Action<IConfigurationBuilder>[] sources)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.Services.AddLogging(logging =>
        {
            if (loggerProvider is not null)
            {
                logging.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Information);
            }
        });

        foreach (Action<IConfigurationBuilder> source in sources)
        {
            source(builder.Configuration);
        }

        builder.AddCbixWorker();

        return builder.Build();
    }

    private string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cbix-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }

    /// <summary>
    /// A real JSON file provider over a file with the given name, so the provenance guard sees the
    /// same provider type and path a deployed worker would.
    /// </summary>
    private Action<IConfigurationBuilder> TrackedConfigurationFile(string fileName, string json)
    {
        string path = Path.Combine(CreateTemporaryDirectory(), fileName);
        File.WriteAllText(path, json);

        return builder => builder.AddJsonFile(path, optional: false, reloadOnChange: false);
    }

    /// <summary>Collects formatted log messages so a test can assert on what startup recorded.</summary>
    /// <remarks>
    /// Formatted rather than structured on purpose: the assertions are "does the endpoint appear"
    /// and "does the credential not appear", and both are questions about the text an operator or a
    /// log sink actually sees. A structured-state assertion would pass while the rendered message
    /// leaked something.
    /// </remarks>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        internal IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
            // Nothing to release: the sink is an in-memory list owned by the test.
        }

        private void Record(string message)
        {
            lock (_messages)
            {
                _messages.Add(message);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                owner.Record(formatter(state, exception));
            }
        }
    }
}
