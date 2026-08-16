using System.Globalization;

using Cbix.Core.Secrets;

using Cbix.Providers.Anthropic;

namespace Cbix.Worker;

/// <summary>
/// Registers everything the CBIX worker needs into a host builder.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the wiring lives here and not in <c>Cbix.Core</c>.</b> CLAUDE.md places the composition
/// root in <c>Cbix.Core</c> so tests exercise the real composition without launching the executable,
/// and that stays the plan for the workflow graph. It cannot hold <em>this</em> wiring: registering
/// the Anthropic adapter means naming <c>AnthropicAgentFactory</c>, and Core is forbidden from
/// referencing a provider adapter in two independently enforced ways
/// (<c>CoreAssemblyNeutralityTests</c>' closed allowlist, and
/// <c>ProviderContainmentTests.CoreAssembly_StillDoesNotKnowTheAdapterExists</c>). That is not an
/// obstacle to work around - it is the LLM-agnosticism constraint doing its job: <em>which</em>
/// provider a deployment uses is a host decision, so the host is the only correct place to make it.
/// The same reasoning covers the configuration binding below, which would drag
/// <c>Microsoft.Extensions.Configuration.Abstractions</c> into Core's neutral reference set.
/// </para>
/// <para>
/// <b>Scope, and what S01-13 does with it.</b> This is the whole composition today because there is
/// nothing else to compose. When S01-13 builds the workflow graph, the neutral half of that graph
/// moves into <c>Cbix.Core</c> and this method shrinks to what only a host can decide: pick the
/// provider, resolve its credential, hand the resulting <c>AIAgent</c> source to Core's composition.
/// The extension keeps its name and <c>Program.cs</c> keeps its three lines.
/// </para>
/// <para>
/// <b>Everything credential-related happens eagerly, here.</b> Resolution, validation and the
/// ambient-environment guard all run while services are being registered - before
/// <c>builder.Build()</c>, let alone before the host starts. Deferring any of it into a service
/// factory would move the failure to the first model call: inside a workflow superstep, after
/// checkpoints, on a document that has already paid for other agents' calls, reported as a 401
/// rather than as "nobody configured this".
/// </para>
/// </remarks>
public static class CbixWorkerHostExtensions
{
    /// <summary>
    /// Configuration key carrying the Anthropic API key, and the logical name the secret is
    /// resolved under.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="AnthropicProviderOptions.SectionName"/> rather than spelled out, so
    /// the secret's name cannot drift from the section the rest of the provider settings bind from.
    /// Populated by user-secrets in local development (the project carries a <c>UserSecretsId</c>;
    /// the provider is registered only in the Development environment), by
    /// <c>Cbix__Providers__Anthropic__ApiKey</c> in a container, and by the bank's secrets-manager
    /// configuration provider in production. Never by a tracked file or the command line - which
    /// <see cref="AnthropicSecretSources"/> enforces at run time, over the providers actually
    /// loaded, and the two static asset scans enforce over the files in the repository.
    /// </remarks>
    public const string AnthropicApiKeySecretName = AnthropicProviderOptions.SectionName + ":ApiKey";

    /// <summary>
    /// Registers the worker's services: the clock, the secret resolver, the Anthropic agent factory
    /// and the background service.
    /// </summary>
    /// <param name="builder">The host builder to register into.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecretNotFoundException">
    /// No configured source carries the Anthropic API key. The message names every source consulted.
    /// </exception>
    /// <exception cref="SecretGovernanceException">
    /// A configuration provider that may not carry a credential - a tracked file, the command line,
    /// or an unrecognised provider type - supplied the API key.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A provider setting is absent or out of range, or a conflicting ambient credential
    /// (<c>ANTHROPIC_AUTH_TOKEN</c>) is present.
    /// </exception>
    public static IHostApplicationBuilder AddCbixWorker(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Inject the clock rather than letting code reach for DateTime.Now / DateTimeOffset.Now.
        // Tests substitute a FakeTimeProvider, and every recorded timestamp stays UTC.
        builder.Services.AddSingleton(TimeProvider.System);

        ISecretResolver secretResolver = AnthropicSecretSources.CreateResolver(builder.Configuration);
        builder.Services.AddSingleton(secretResolver);

        // Resolved and validated now, not in the factory lambda below: this is what makes a missing
        // or conflicting credential a startup failure. The populated options object is captured in
        // the closure rather than registered, because it carries the key and nothing else in the
        // graph has any business resolving it.
        AnthropicProviderOptions options = ReadAnthropicOptions(builder.Configuration, secretResolver);
        options.Validate();

        // A factory lambda rather than a pre-built instance: the container disposes services it
        // creates, and the factory owns an HTTP client that must be closed with the host. An
        // instance registered directly would never be disposed. Validation has already run, so
        // nothing is deferred except the allocation.
        builder.Services.AddSingleton(serviceProvider =>
        {
            AnthropicAgentFactory factory = new(options);
            EmitEndpointEvent(serviceProvider, factory);
            return factory;
        });

        builder.Services.AddHostedService(serviceProvider =>
        {
            // Resolving the factory here is deliberate, not incidental. It constructs the provider
            // client - and so emits the endpoint event - during host start, rather than whenever
            // something first happens to need an agent. An endpoint recorded after work has begun
            // is not a startup record.
            _ = serviceProvider.GetRequiredService<AnthropicAgentFactory>();

            return ActivatorUtilities.CreateInstance<Worker>(serviceProvider);
        });

        return builder;
    }

    /// <summary>
    /// Records, once at startup, which endpoint every model call in this run will go to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this closes.</b> The endpoint is already protected against <em>ambient</em>
    /// redirection - <c>ANTHROPIC_BASE_URL</c> was measured being ignored because the adapter always
    /// sets the base URL explicitly. It is deliberately <em>not</em> protected against configured
    /// redirection: <c>BaseUrl</c> in <c>appsettings.json</c>, or
    /// <c>--Cbix:Providers:Anthropic:BaseUrl=...</c>, is a blessed configuration change, because
    /// design 8 anticipates an approved egress proxy and pinning the endpoint must not mean
    /// hard-coding it. The gap was that such a redirect left no trace at all: the API key and the
    /// document content would go somewhere else and nothing would say so. One event makes a silent
    /// redirect an observable one.
    /// </para>
    /// <para>
    /// Safe to log: <see cref="AnthropicAgentFactory.ResolvedEndpoint"/> is documented as
    /// credential-free, unlike the client options it derives from - whose <c>ToString()</c> was
    /// measured printing the API key in clear.
    /// </para>
    /// <para>
    /// <b>An endpoint allowlist was considered and deferred.</b> Refusing any host outside an
    /// approved set is the stronger control, but the approved set is a deployment fact nobody has
    /// yet - there is no egress proxy to name - so it would be invented rather than configured. It
    /// belongs with the telemetry and observability story, where this event also gains a trace
    /// attribute and the allowlist has a real source.
    /// </para>
    /// </remarks>
    private static void EmitEndpointEvent(IServiceProvider serviceProvider, AnthropicAgentFactory factory)
    {
        ILogger<AnthropicAgentFactory> logger =
            serviceProvider.GetRequiredService<ILogger<AnthropicAgentFactory>>();

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Anthropic provider endpoint resolved to {AnthropicEndpoint}. All model calls in this run "
                    + "go to this host.",
                factory.ResolvedEndpoint);
        }
    }

    /// <summary>
    /// Reads the provider's non-secret settings from configuration and injects the resolved key.
    /// </summary>
    /// <remarks>
    /// The three knobs are read one by one instead of with <c>Bind</c>, and that is the point: a
    /// binder pointed at this section would happily populate <c>ApiKey</c> from whichever provider
    /// carried it, making the "the key comes from the secret resolver" rule a convention rather than
    /// a structural fact. Reading named keys means the only assignment to <c>ApiKey</c> in this
    /// solution's composition is the one below. It also keeps the host off
    /// <c>Microsoft.Extensions.Configuration.Binder</c> for three scalars.
    /// </remarks>
    private static AnthropicProviderOptions ReadAnthropicOptions(
        IConfiguration configuration,
        ISecretResolver secretResolver)
    {
        IConfigurationSection section = configuration.GetSection(AnthropicProviderOptions.SectionName);
        AnthropicProviderOptions options = new();

        if (section["BaseUrl"] is { Length: > 0 } baseUrl)
        {
            options.BaseUrl = baseUrl;
        }

        if (section["ModelId"] is { Length: > 0 } modelId)
        {
            options.ModelId = modelId;
        }

        if (section["MaxOutputTokens"] is { Length: > 0 } maxOutputTokens)
        {
            options.MaxOutputTokens = ParseMaxOutputTokens(maxOutputTokens);
        }

        options.ApiKey = secretResolver.Require(AnthropicApiKeySecretName);

        return options;
    }

    private static int ParseMaxOutputTokens(string configured) =>
        int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration key '{AnthropicProviderOptions.SectionName}:MaxOutputTokens' is "
                    + $"'{configured}', which is not an integer.");
}
