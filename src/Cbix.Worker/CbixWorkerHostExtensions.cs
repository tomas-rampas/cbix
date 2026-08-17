using System.Globalization;

using Cbix.Core.Agents;
using Cbix.Core.Diagnostics;
using Cbix.Core.Documents;
using Cbix.Core.Hosting;
using Cbix.Core.Ingest;
using Cbix.Core.Secrets;
using Cbix.Core.Workflows;

using Cbix.Providers.Anthropic;

using Microsoft.Agents.AI;

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
/// <b>Scope, as S01-13 settled it.</b> The graph itself is composed by
/// <see cref="CbixCoreServiceCollectionExtensions.AddCbixWorkflow"/> in <c>Cbix.Core</c>, so tests -
/// including S01-09's stub-client agnosticism run - exercise the real composition without this
/// executable. What is left here is exactly what only a host can decide, and each item is here for
/// a reason that would not survive being moved:
/// </para>
/// <list type="bullet">
///   <item><description>
///   <b>Which provider.</b> Registering it means naming <see cref="AnthropicAgentFactory"/>, which
///   Core may not do.
///   </description></item>
///   <item><description>
///   <b>Its credential.</b> Resolved and validated eagerly, below.
///   </description></item>
///   <item><description>
///   <b>The agents themselves.</b> Constructing one means choosing a model tier and a snapshot -
///   provider facts. Core asks for them by role, as keyed <see cref="AIAgent"/> registrations.
///   </description></item>
///   <item><description>
///   <b>The native-document profile source.</b> Only an adapter holding the provider client can
///   build that profile; Core contributes the two that need no provider.
///   </description></item>
///   <item><description>
///   <b>Configuration binding.</b> The ingest root, the size cap and the provider's presentation
///   capability are read here, because binding them in Core would drag
///   <c>Microsoft.Extensions.Configuration.Abstractions</c> into its neutral reference set for a
///   handful of scalars.
///   </description></item>
/// </list>
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

    /// <summary>Configuration section carrying the ingest gate's settings.</summary>
    public const string IngestSectionName = "Cbix:Ingest";

    /// <summary>
    /// Configuration key naming what the configured provider can be shown a document as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Provider-neutral by name and by section, deliberately. It states a capability
    /// (<c>NativeDocument</c>, <c>Vision</c>, <c>TextOnly</c>), never a profile class, so an operator
    /// behind an egress proxy that blocks the Files API sets a capability and the composed graph picks
    /// a different profile with no code change - which is design 5.1's "only the strategy
    /// implementations know which profile is in play", made operable.
    /// </para>
    /// <para>
    /// It is <em>not</em> under the Anthropic section: the value describes the deployment's provider,
    /// whichever that is, and burying it under one vendor's key would make swapping vendor a
    /// configuration rewrite rather than a configuration change.
    /// </para>
    /// </remarks>
    public const string DocumentPresentationKey = "Cbix:Provider:DocumentPresentation";

    /// <summary>
    /// Configuration key carrying the confidence at or above which triage sends a document into
    /// extraction rather than to a human.
    /// </summary>
    /// <remarks>
    /// Provider-neutral by name and by section, like the presentation key above and unlike anything
    /// under the Anthropic section: the threshold describes how much this deployment trusts triage, not
    /// which vendor answers it. Design 11 expects the number to move once correction rates exist, and
    /// this key is what makes that a configuration change.
    /// </remarks>
    public const string TriageReviewThresholdKey = "Cbix:Triage:ReviewConfidenceThreshold";

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
    /// A provider setting is absent or out of range, a conflicting ambient credential
    /// (<c>ANTHROPIC_AUTH_TOKEN</c>) is present, or a configured value cannot be parsed - a
    /// non-integer output-token cap or maximum document size, or a document-presentation capability
    /// that is not one of the known ones.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The configured ingest root is unusable: a UNC or device-namespace path, or one that cannot be
    /// made fully qualified. Raised at composition, which is the point - an ingest root that can
    /// never contain anything is a deployment mistake, not a per-document failure.
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

        // The provider's own contribution to document presentation. Registered before Core's
        // composition so the two local profiles it adds join this one in the selector's index; the
        // selector picks among all three from the configured capability alone.
        builder.Services.AddSingleton<IDocumentContentProfileSource, ClaudeDocumentContentProfileSource>();

        // The agents Core's graph asks for by role. Keyed rather than named-by-type because the graph
        // will hold nine of them by Sprint 02 and they are all the same abstraction; the key IS the
        // role.
        //
        // A FACTORY rather than a bare AIAgent, and story S01-14 is where that changed. An agent
        // registered on its own can only be run without a document - and a triage call made without
        // the document is a model identifying something it was never shown, answering fluently, with
        // nothing in the run to say so. Registering the factory means the node's only way to obtain an
        // agent already has this run's document attached, and how that document reaches the wire
        // (Claude's escape hatch, or ordinary chat content when a proxy forces a degraded
        // presentation) is settled inside the adapter rather than at the node.
        //
        // Singleton, not scoped. The factory is a stateless caller over the adapter's pooled client -
        // the per-run state lives in the document-content provider, which is scoped, and in the
        // per-document binding the factory hands out. Resolved lazily inside the lambda so that
        // composing a host without ever running the workflow (which every credential test does)
        // constructs nothing.
        builder.Services.AddKeyedSingleton<IDocumentBoundAgentFactory>(
            CbixWorkflowNodes.Triage,
            (serviceProvider, _) => serviceProvider.GetRequiredService<AnthropicAgentFactory>()
                .CreateDocumentAgentFactory(
                    name: CbixWorkflowNodes.Triage,

                    // No model named, which IS the tiering decision: the configured default is the
                    // Haiku snapshot design 7 assigns to triage. Naming it again here would be a second
                    // place for the tier to drift from the pin.
                    instructions: TriageInstructions));

        // The first section agent (story S01-16). Design 5.4 puts DocControl on the Haiku tier, which
        // is the configured default - so, as with triage, naming a model here would be a second place
        // for the tier to drift. The Matrix agent is the one that will name Sonnet explicitly when
        // Sprint 02 registers it, because it is the one the design tiers differently.
        builder.Services.AddKeyedSingleton<IDocumentBoundAgentFactory>(
            CbixWorkflowNodes.SectionExtraction,
            (serviceProvider, _) => serviceProvider.GetRequiredService<AnthropicAgentFactory>()
                .CreateDocumentAgentFactory(
                    name: CbixWorkflowNodes.SectionExtraction,
                    instructions: SectionExtractionInstructions));

        // Core's half: the ingest stack, the local document-content profiles and the workflow graph.
        // Everything above is what this call cannot decide for itself.
        builder.Services.AddCbixWorkflow(
            ReadIngestOptions(builder.Configuration, builder.Environment.ContentRootPath),
            ReadDocumentPresentationOptions(builder.Configuration),
            ReadTriageRoutingOptions(builder.Configuration));

        // Scope validation, in PRODUCTION and not only in tests. Measured: with the default provider
        // options a scoped service resolved from the root container is silently promoted to a
        // root-lifetime singleton - no error, no log. For CBIX that promotion is the exact hazard the
        // run-scoped document-content profile exists to prevent: the "prepare each document once" memo
        // would become process-wide, holding every run's uploaded file ids and rendered page images for
        // as long as the worker lives, and one run's prepared document would be handed to the next.
        // The tests build their containers with ValidateScopes on and would never see it; only the
        // deployed host would, as unbounded memory growth and cross-run contamination.
        //
        // ValidateOnBuild moves an unresolvable registration from "throws inside the first superstep of
        // a real run" to "the host does not start", which is the same argument the credential
        // resolution above already makes.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        }));

        builder.Services.AddHostedService(serviceProvider =>
        {
            // Everything in this lambda runs when the host resolves its hosted services - i.e. at
            // start, before any document is processed. That timing is the whole point: each of these
            // would otherwise surface mid-batch, on a document that has already paid for work.

            // Resolving the factory here is deliberate, not incidental. It constructs the provider
            // client - and so emits the endpoint event - during host start, rather than whenever
            // something first happens to need an agent. An endpoint recorded after work has begun
            // is not a startup record.
            _ = serviceProvider.GetRequiredService<AnthropicAgentFactory>();

            // Two distinct composition faults, and each needs its own probe because they are detected
            // at different moments.
            //
            // Resolving the SELECTOR runs its constructor, which indexes the registered profile
            // sources and refuses two claiming the same capability. That is a wiring conflict - a host
            // registering a second native-document source, say - and until now it surfaced only when
            // the first run asked for a provider.
            DocumentContentProfileSelector selector =
                serviceProvider.GetRequiredService<DocumentContentProfileSelector>();

            EmitDocumentPresentationEvent(serviceProvider, selector);

            // Resolving an actual PROVIDER runs the selector's lookup, which refuses a configured
            // capability that no registered source implements. The constructor above cannot catch that:
            // it indexes what exists without asking what was asked for.
            //
            // A throwaway scope, because the provider is run-scoped and resolving it from the root
            // would be the very promotion ValidateScopes now forbids. Creating one costs an object and
            // is safe: constructing a profile performs no I/O - no upload, no render, no network - so
            // this probe cannot touch a document or spend anything. Disposed immediately, so the memo
            // it would have held dies with it.
            using IServiceScope probe = serviceProvider.CreateScope();
            _ = probe.ServiceProvider.GetRequiredService<IDocumentContentProvider>();

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
                new EventId(CbixEventIds.HostProviderEndpointResolved),
                "Anthropic provider endpoint resolved to {AnthropicEndpoint}. All model calls in this run "
                    + "go to this host.",
                factory.ResolvedEndpoint);
        }
    }

    /// <summary>
    /// Records, once at startup, how documents in this run will be presented to the model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The symmetric half of <see cref="EmitEndpointEvent"/>, and it answers the other half of the
    /// egress question.</b> That event says <em>where</em> the traffic goes; this says <em>what goes
    /// in it</em>. The three capabilities send materially different things: the native-document
    /// profile uploads the whole PDF to the provider's Files API and leaves it there, the
    /// generic-vision profile sends locally rendered images of every page, and the text-only profile
    /// sends the extracted text alone. A deployment reviewer looking at one startup line should be
    /// able to say which of those a run did, without reading configuration on a machine they may not
    /// have.
    /// </para>
    /// <para>
    /// <b>It also makes the default's consequence visible.</b> An unconfigured deployment presents
    /// documents natively, which means whole documents leave the estate. That is not a hidden
    /// decision - it cannot happen without an Anthropic API key having been deliberately provisioned
    /// through the secrets manager, and the worker refuses to start without one - but "not hidden"
    /// and "recorded" are different things, and only the second survives an audit.
    /// </para>
    /// <para>
    /// The capability is an enum this code chose from a closed set, so there is nothing to sanitise:
    /// no operator-supplied string reaches this line.
    /// </para>
    /// </remarks>
    private static void EmitDocumentPresentationEvent(
        IServiceProvider serviceProvider,
        DocumentContentProfileSelector selector)
    {
        ILogger<DocumentContentProfileSelector> logger =
            serviceProvider.GetRequiredService<ILogger<DocumentContentProfileSelector>>();

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                new EventId(CbixEventIds.HostDocumentPresentationResolved),
                "Documents in this run are presented to the model as {DocumentPresentation}. This decides "
                    + "what leaves the estate: the whole document file, rendered page images, or extracted "
                    + "text only.",
                selector.ConfiguredCapability);
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

    /// <summary>
    /// The triage agent's system instructions: the uniform extraction prompting rules and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The split between these and the turn triage sends is deliberate.</b> These are the rules
    /// every CBIX agent obeys (CLAUDE.md), stated once at the place where an agent is constructed -
    /// which is here, because constructing one means choosing a provider. Triage's own contract - the
    /// <c>DocumentProfile</c> fields, the UNKNOWN convention, the JSON-only requirement - lives on the
    /// turn, in <c>Cbix.Core</c>'s <c>TriageExecutor</c>, because it is provider-independent and
    /// because it has to be built from the same field list the parser enforces.
    /// </para>
    /// <para>
    /// The model tier is not named here either: the configured default is the Haiku snapshot design 7
    /// assigns to triage, and naming it a second time would be a second place for it to drift from
    /// the pin.
    /// </para>
    /// </remarks>
    private const string TriageInstructions =
        "You identify cross-border trading legal instruction documents. Extract, never interpret. "
            + "Copy values verbatim. Use only the supplied document. Report page numbers as they are "
            + "shown in a PDF viewer. Never guess at a document you do not recognise: say so instead, "
            + "in the form the turn asks for.";

    /// <summary>
    /// A section agent's system instructions: design Appendix B's system prompt, verbatim in substance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The uniform rules again, in the section agents' own words, because what a section agent must
    /// never do differs in emphasis from what triage must never do: triage's hazard is guessing at a
    /// document class, a section agent's is producing a value it did not read and a snippet it did not
    /// copy. Design 5.6 makes verbatim containment the gate that catches the second, so the
    /// instruction to copy exactly is stated as the load-bearing rule it is.
    /// </para>
    /// <para>
    /// Shared across all seven agents deliberately: the per-section contract lives on the turn, which
    /// is where <c>DocControlExecutor</c> puts it and where Sprint 02's six siblings will put theirs.
    /// </para>
    /// </remarks>
    private const string SectionExtractionInstructions =
        "You extract data from a cross-border trading legal instruction. Use ONLY the supplied "
            + "document. Copy SourceSnippet values VERBATIM from the document text - the exact "
            + "characters, never a tidied or re-punctuated version. Use logical page numbers as shown "
            + "in a PDF viewer. If a field is absent, return null - never guess, and never take a value "
            + "from anywhere but this document. Return JSON matching the requested schema exactly.";

    /// <summary>Reads the ingest gate's settings, defaulting the root under the content root.</summary>
    /// <remarks>
    /// <para>
    /// <b>The default root is deliberately a path, not an absence.</b> Making the key required would
    /// make every host that never ingests anything - the credential tests, a diagnostic run - fail to
    /// compose, which trades a real cost for a hypothetical one. The default is a directory under the
    /// content root, so an unconfigured deployment reads from a location it owns rather than from
    /// wherever the process happens to be running.
    /// </para>
    /// <para>
    /// <b>The size cap defaults to the Claude Files API limit</b> rather than to something rounder:
    /// the cap exists so a document that could never be uploaded is refused before it is read and
    /// hashed, and a default above the provider's own ceiling would let exactly those through.
    /// </para>
    /// </remarks>
    private static DocumentIngestOptions ReadIngestOptions(IConfiguration configuration, string contentRootPath)
    {
        IConfigurationSection section = configuration.GetSection(IngestSectionName);

        string root = section["Root"] is { Length: > 0 } configured
            ? configured
            : Path.Combine(contentRootPath, "ingest");

        long maxBytes = section["MaxDocumentBytes"] is { Length: > 0 } cap
            ? ParseMaxDocumentBytes(cap)
            : DocumentIngestOptions.ClaudeFilesApiLimitBytes;

        return new DocumentIngestOptions(Path.GetFullPath(root), maxBytes);
    }

    /// <summary>Reads where triage's review edge sits.</summary>
    /// <remarks>
    /// <para>
    /// Read here rather than defaulted in Core because it is a deployment decision, and a decision the
    /// first pilot batch is expected to change: design 11 says confidence thresholds are calibrated
    /// against observed correction rates, and nobody has those rates yet. Making it configuration is
    /// what turns that recalibration into an edit to a config map rather than a release.
    /// </para>
    /// <para>
    /// Parsed strictly and invariantly. Strictly for the same reason as the presentation capability: a
    /// typo that fell back to the default would move the review gate without saying so, and the only
    /// symptom would be documents extracted that should have been queued - which nobody reports,
    /// because everything still gets done. Invariantly because a decimal comma on a German-locale host
    /// would parse "0,9" as 9 on one machine and fail on another, and a threshold that depends on the
    /// host's locale is not a threshold.
    /// </para>
    /// <para>
    /// Range validation is left to <see cref="TriageRoutingOptions"/>, which owns it: a value outside
    /// the unit interval disables the gate in one direction or the other, and the refusal belongs with
    /// the invariant rather than duplicated at every reader.
    /// </para>
    /// </remarks>
    private static TriageRoutingOptions ReadTriageRoutingOptions(IConfiguration configuration)
    {
        if (configuration[TriageReviewThresholdKey] is not { Length: > 0 } configured)
        {
            return new TriageRoutingOptions();
        }

        if (!double.TryParse(configured, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold))
        {
            throw new InvalidOperationException(
                $"Configuration key '{TriageReviewThresholdKey}' is '{ForMessage(configured)}', which is not "
                    + "a number. It is the confidence at or above which a document is extracted rather than "
                    + "queued for a human, so a value that cannot be read is a gate that cannot be placed.");
        }

        return new TriageRoutingOptions(threshold);
    }

    /// <summary>Reads the configured provider's document-presentation capability.</summary>
    /// <remarks>
    /// Parsed strictly and case-insensitively: an unrecognised value is a refusal, not a fallback. A
    /// typo silently resolving to the default would present documents differently from what was
    /// configured and show up only as degraded matrix accuracy - the failure mode design 5.1 exists
    /// to prevent, arriving by way of a spelling mistake.
    /// </remarks>
    private static DocumentPresentationOptions ReadDocumentPresentationOptions(IConfiguration configuration)
    {
        if (configuration[DocumentPresentationKey] is not { Length: > 0 } configured)
        {
            return new DocumentPresentationOptions();
        }

        // The name check comes FIRST, and it is not redundant with TryParse. Enum.TryParse accepts a
        // DIGIT STRING for any integer, defined or not: "0" parses to NativeDocument and "2" to
        // TextOnly, so a configuration value of "2" - a plausible typo, or a leftover from a numeric
        // scheme - would silently select the degraded text-only presentation with no error. Worse,
        // Enum.IsDefined then passes, because the value genuinely is defined. Requiring the configured
        // string to be one of the NAMES makes the numeric back door unreachable, and every unrecognised
        // value - digit or not - lands on the same refusal below.
        bool namesACapability = Array.Exists(
            Enum.GetNames<DocumentPresentationCapability>(),
            name => string.Equals(name, configured, StringComparison.OrdinalIgnoreCase));

        if (!namesACapability
            || !Enum.TryParse(configured, ignoreCase: true, out DocumentPresentationCapability capability)
            || !Enum.IsDefined(capability))
        {
            throw new InvalidOperationException(
                $"Configuration key '{DocumentPresentationKey}' is '{ForMessage(configured)}', which is not "
                    + $"a known document-presentation capability. Valid values: "
                    + $"{string.Join(", ", Enum.GetNames<DocumentPresentationCapability>())}.");
        }

        return new DocumentPresentationOptions(capability);
    }

    /// <summary>
    /// Renders an operator-supplied configuration value safely inside an exception message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same reasoning as <c>PathBoundary.ForLog</c>, applied to the other untrusted string
    /// class this process handles.</b> A configuration value is not attacker-controlled in the way a
    /// dropped file name is, but it is not written by the person who reads the failure either: it
    /// arrives from an environment variable, a Helm chart or a mounted config map, and the exception
    /// carrying it is what an operator reads in a terminal and what a log sink stores as a line.
    /// Control characters, format characters (U+200E and friends) and line separators in that string
    /// forge log lines and reorder rendered text.
    /// </para>
    /// <para>
    /// <b>Duplicated here rather than exposed from Core.</b> <c>PathBoundary</c> is internal to
    /// <c>Cbix.Core</c> and its <c>ForLog</c> carries a path-length truncation this has no use for.
    /// Widening Core's public surface to share eight lines would trade a real API commitment for a
    /// small saving; the two are kept in step by the comment on each rather than by a shared type.
    /// The category list is the same one <c>PathBoundary.IsUnsafeToRender</c> uses.
    /// </para>
    /// </remarks>
    private static string ForMessage(string configured)
    {
        // Bounded before rendering: a multi-megabyte environment variable in an exception message is a
        // denial of service against whoever has to read it.
        const int MaxRenderedLength = 200;
        const string TruncationMarker = "...[truncated]";

        ReadOnlySpan<char> kept = configured.Length <= MaxRenderedLength
            ? configured
            : configured.AsSpan(0, MaxRenderedLength);

        string sanitised = string.Create(kept.Length, configured, static (destination, original) =>
        {
            for (int index = 0; index < destination.Length; index++)
            {
                char character = original[index];

                destination[index] = char.IsControl(character)
                    || char.GetUnicodeCategory(character)
                        is UnicodeCategory.Format
                        or UnicodeCategory.Surrogate
                        or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator
                    ? '�'
                    : character;
            }
        });

        // Appended after sanitisation, so nothing in the input can forge the marker.
        return configured.Length <= MaxRenderedLength ? sanitised : sanitised + TruncationMarker;
    }

    private static long ParseMaxDocumentBytes(string configured) =>
        long.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration key '{IngestSectionName}:MaxDocumentBytes' is '{ForMessage(configured)}', which is "
                    + "not an integer.");

    private static int ParseMaxOutputTokens(string configured) =>
        int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration key '{AnthropicProviderOptions.SectionName}:MaxOutputTokens' is "
                    + $"'{ForMessage(configured)}', which is not an integer.");
}
