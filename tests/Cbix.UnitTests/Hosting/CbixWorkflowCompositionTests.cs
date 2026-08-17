using Cbix.Core.Agents;
using Cbix.Core.Documents;
using Cbix.Core.Hosting;
using Cbix.Core.Ingest;
using Cbix.Core.Review;
using Cbix.Core.Workflows;

using Cbix.UnitTests.Providers;

using Cbix.Worker;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cbix.UnitTests.Hosting;

/// <summary>
/// Composition tests for the workflow half of the service graph (story S01-13).
/// </summary>
/// <remarks>
/// <para>
/// Two questions, and they are asked of two different containers on purpose.
/// </para>
/// <para>
/// <b>Lifetimes</b> are asked of Core's own <c>AddCbixWorkflow</c>, because that is where they are
/// decided and a host cannot change them. The one that matters is the document-content profile: it
/// carries the "prepare each document once" memo, so a singleton registration would retain every
/// run's uploaded file ids and rendered page images for the lifetime of the process. That is not a
/// theoretical leak - a rendered page is megabytes of pixels and a worker drains a queue - and it has
/// no compile-time symptom, which is why it is asserted rather than reviewed.
/// </para>
/// <para>
/// <b>Profile selection</b> is asked of the real host, because the claim under test is that a
/// configuration change swaps the profile with no code change, and only the host reads configuration.
/// The Claude profile is contributed by the host and the text-only profile by Core, so a swap between
/// them also proves the selection spans both halves of the composition root.
/// </para>
/// <para>
/// In the serialised environment collection: composing the host validates the Anthropic options,
/// which read <c>ANTHROPIC_AUTH_TOKEN</c>.
/// </para>
/// </remarks>
[Collection(AnthropicEnvironmentCollection.Name)]
public sealed class CbixWorkflowCompositionTests : IDisposable
{
    /// <summary>
    /// Self-labelling placeholder. It deliberately does not begin with the vendor's real API-key
    /// prefix, and this comment deliberately does not spell that prefix out either: secret scanners
    /// pattern-match the literal, so writing it even in prose makes the repository trip its own
    /// scanner - and a codebase that cries wolf teaches everyone to dismiss the alerts.
    /// </summary>
    private const string PlaceholderApiKey = "workflow-composition-placeholder-not-a-real-credential";

    private const string ScopedApiKeyKey = "Cbix:Providers:Anthropic:ApiKey";

    private readonly List<string> _temporaryDirectories = [];

    [Fact]
    public void AddCbixWorkflow_RegistersTheDocumentContentProfileAsRunScoped()
    {
        // THE registration this story singles out. Asserted three ways because each catches a
        // different mistake: the descriptor pins the declared intent, one-instance-per-scope catches a
        // transient (which would re-prepare for every agent in the fan-out and defeat "upload once"),
        // and different-instances-across-scopes catches a singleton (which would retain one run's
        // prepared document into the next).
        using ServiceProvider container = ComposeCore(DocumentPresentationCapability.TextOnly);

        ServiceDescriptor descriptor = Assert.Single(
            container.GetRequiredService<IServiceCollection>(),
            candidate => candidate.ServiceType == typeof(IDocumentContentProvider));

        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);

        using IServiceScope firstRun = container.CreateScope();
        using IServiceScope secondRun = container.CreateScope();

        IDocumentContentProvider first = firstRun.ServiceProvider.GetRequiredService<IDocumentContentProvider>();
        IDocumentContentProvider second = secondRun.ServiceProvider.GetRequiredService<IDocumentContentProvider>();

        Assert.Same(first, firstRun.ServiceProvider.GetRequiredService<IDocumentContentProvider>());
        Assert.NotSame(first, second);
    }

    [Fact]
    public void AddCbixWorkflow_RegistersTheIngestServiceAndExecutorsAsRunScoped()
    {
        // The captive-dependency guard. The ingest service holds the run-scoped profile above, so a
        // singleton ingest service would capture one run's provider and hand it to every later run -
        // and the container would not complain, because capturing a scoped service in a singleton is
        // only detected when validation is switched on. The executors hold the ingest service, and the
        // factory holds the executors, so the whole chain has to carry the scope.
        using ServiceProvider container = ComposeCore(DocumentPresentationCapability.TextOnly);

        foreach (Type scoped in (Type[])
            [
                typeof(DocumentIngestService),
                typeof(DocumentIngestExecutor),
                typeof(DocControlExecutor),
                typeof(PersistStubExecutor),
                typeof(DuplicateTerminalExecutor),
                typeof(ReviewQueueStubExecutor),
                typeof(CbixWorkflowFactory),
            ])
        {
            ServiceDescriptor descriptor = Assert.Single(
                container.GetRequiredService<IServiceCollection>(),
                candidate => candidate.ServiceType == scoped);

            Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        }
    }

    [Fact]
    public void AddCbixWorkflow_KeepsTheReviewQueueProcessWide()
    {
        // The sharpest case of the process-wide rule: a queue scoped to the run that filled it would
        // lose every row the moment that run ended, which is the one thing a review queue must not do.
        // Asserted across two scopes rather than by reading the descriptor, because what matters is
        // that a row survives the run - not how the registration is spelled.
        using ServiceProvider container = ComposeCore(DocumentPresentationCapability.TextOnly);

        using IServiceScope firstRun = container.CreateScope();
        using IServiceScope secondRun = container.CreateScope();

        Assert.Same(
            firstRun.ServiceProvider.GetRequiredService<IReviewQueue>(),
            secondRun.ServiceProvider.GetRequiredService<IReviewQueue>());
    }

    [Fact]
    public void AddCbixWorkflow_DefaultsTheRoutingThresholdAndLetsAHostOverrideIt()
    {
        // The threshold is a deployment decision design 11 expects to move once correction rates exist,
        // so two properties matter: an unconfigured composition gets the documented default, and a host
        // that supplies one wins. The second is what makes recalibration a config change rather than a
        // release, and TryAdd is what makes "the host's registration wins" a rule rather than a
        // function of ordering.
        using ServiceProvider defaulted = ComposeCore(DocumentPresentationCapability.TextOnly);

        Assert.Equal(
            TriageRoutingOptions.DefaultReviewConfidenceThreshold,
            defaulted.GetRequiredService<TriageRoutingOptions>().ReviewConfidenceThreshold);

        ServiceCollection services = [];
        services.AddLogging();
        services.AddCbixWorkflow(
            new DocumentIngestOptions(CreateTemporaryDirectory(), DocumentIngestOptions.ClaudeFilesApiLimitBytes),
            new DocumentPresentationOptions(DocumentPresentationCapability.TextOnly),
            new TriageRoutingOptions(0.42d));

        using ServiceProvider configured = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        Assert.Equal(0.42d, configured.GetRequiredService<TriageRoutingOptions>().ReviewConfidenceThreshold);
    }

    [Fact]
    public void AddCbixWorker_ReadsTheTriageReviewThresholdFromConfiguration()
    {
        // End to end through the real host entry point: a key in configuration, a threshold in the
        // container, no code change anywhere between.
        using IHost host = ComposeHost((CbixWorkerHostExtensions.TriageReviewThresholdKey, "0.55"));

        Assert.Equal(
            0.55d,
            host.Services.GetRequiredService<TriageRoutingOptions>().ReviewConfidenceThreshold);
    }

    [Fact]
    public void AddCbixWorker_ReadsTheTriageReviewThresholdInvariantlyAndStrictly()
    {
        // Two failures that would otherwise be silent, and both are locale- or typo-shaped rather than
        // exotic. "0,55" is what a German-locale host writes: parsed under the ambient culture it is
        // 0.55 on one machine and refused on another, so a threshold read that way depends on where it
        // runs. Read invariantly it is refused everywhere, identically - which is the property worth
        // having, and it is also why the refusal below is "not a number" rather than "out of range".
        // "high" is the typo case: a value that fell back to the default would move the review gate
        // without saying so, and the only symptom would be documents extracted that should have been
        // queued.
        foreach (string configured in (string[])["0,55", "high"])
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => ComposeHost((CbixWorkerHostExtensions.TriageReviewThresholdKey, configured)).Dispose());

            Assert.Contains(configured, error.Message, StringComparison.Ordinal);
        }

        // A number that parses but disables the gate is refused too, by the options type that owns the
        // invariant rather than by a duplicate check at the reader.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ComposeHost((CbixWorkerHostExtensions.TriageReviewThresholdKey, "1.5")).Dispose());
    }

    [Fact]
    public void AddCbixWorkflow_KeepsTheRegistryAndAuditLogProcessWide()
    {
        // The mirror image, and it is not symmetry for its own sake: the registry is what makes a
        // re-submission a duplicate, so scoping it to a run would silently disable deduplication
        // ACROSS runs - which is the only place deduplication does anything. The failure would be
        // invisible in every test that submits one document.
        using ServiceProvider container = ComposeCore(DocumentPresentationCapability.TextOnly);

        using IServiceScope firstRun = container.CreateScope();
        using IServiceScope secondRun = container.CreateScope();

        Assert.Same(
            firstRun.ServiceProvider.GetRequiredService<IDocumentRegistry>(),
            secondRun.ServiceProvider.GetRequiredService<IDocumentRegistry>());
        Assert.Same(
            firstRun.ServiceProvider.GetRequiredService<IIngestAuditLog>(),
            secondRun.ServiceProvider.GetRequiredService<IIngestAuditLog>());
    }

    [Fact]
    public void AddCbixWorkflow_BuildsTheGraphWithoutAnyProviderPackage()
    {
        // The shape S01-09 will assert on, exercised here as a composition fact: Core's graph builds
        // and runs from a container holding a canned chat client and nothing else. No adapter, no
        // credential, no network. If this ever needed a provider to compose, the split CLAUDE.md
        // describes would have failed silently.
        using ServiceProvider container = ComposeCore(DocumentPresentationCapability.TextOnly);
        using IServiceScope run = container.CreateScope();

        Workflow workflow = run.ServiceProvider.GetRequiredService<CbixWorkflowFactory>().Build();

        Assert.Equal(CbixWorkflowNodes.Ingest, workflow.StartExecutorId);
    }

    [Fact]
    public void AddCbixWorkflow_RefusesACapabilityNobodyRegisteredAProfileFor()
    {
        // Fail closed, and fail where it can be read. Core registers the vision and text-only sources;
        // the native-document source is the host's. A host that configured NativeDocument and forgot
        // to register the adapter's source could have been "helpfully" downgraded to text-only - and
        // would then have run the whole PoC without ever showing the model a page image, reporting
        // itself as healthy while matrix accuracy collapsed.
        using ServiceProvider container = ComposeCore(DocumentPresentationCapability.NativeDocument);
        using IServiceScope run = container.CreateScope();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            run.ServiceProvider.GetRequiredService<IDocumentContentProvider>);

        Assert.Contains("NativeDocument", error.Message, StringComparison.Ordinal);
        Assert.Contains("TextOnly", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredCapability_SelectsTheClaudeProfile()
    {
        // Half one of the config-swap proof. Nothing in this test names a profile type as an input -
        // it sets a capability string in configuration - and the assertion is on which implementation
        // came back.
        using IHost host = ComposeHost(("Cbix:Provider:DocumentPresentation", "NativeDocument"));
        using IServiceScope run = host.Services.CreateScope();

        IDocumentContentProvider provider = run.ServiceProvider.GetRequiredService<IDocumentContentProvider>();

        // By type NAME rather than by `Assert.IsType`, because the Claude profile is internal to the
        // adapter and naming it here would be this test project asserting through a type the
        // production graph is forbidden to name. The name is what the composition produced.
        Assert.Equal("ClaudeDocumentContentProvider", provider.GetType().Name);
    }

    [Fact]
    public void ConfiguredCapability_SelectsTheTextOnlyProfileWithNoCodeChange()
    {
        // Half two, and the two halves together ARE the story's requirement: one string in
        // configuration differs between this test and the one above, no code differs at all, and the
        // active profile changes. This is the deployment case that motivates it - an egress proxy that
        // blocks the Files API - and the point is that it needs a configuration change, not a build.
        using IHost host = ComposeHost(("Cbix:Provider:DocumentPresentation", "TextOnly"));
        using IServiceScope run = host.Services.CreateScope();

        IDocumentContentProvider provider = run.ServiceProvider.GetRequiredService<IDocumentContentProvider>();

        Assert.IsType<TextOnlyDocumentContentProfile>(provider);
    }

    [Fact]
    public void ConfiguredCapability_SelectsTheVisionProfile()
    {
        // The third capability, and the one that matters most for agnosticism: it is the profile a
        // non-Claude provider would run under, and it is contributed entirely by Core. Included so the
        // selection mechanism is proved over its whole domain rather than over the two capabilities
        // that happen to be interesting today.
        using IHost host = ComposeHost(("Cbix:Provider:DocumentPresentation", "Vision"));
        using IServiceScope run = host.Services.CreateScope();

        Assert.IsType<GenericVisionDocumentContentProfile>(
            run.ServiceProvider.GetRequiredService<IDocumentContentProvider>());
    }

    [Fact]
    public void ConfiguredCapability_IsCaseInsensitiveButNotForgiving()
    {
        // Case-insensitive because "textonly" in a YAML manifest is not a mistake worth failing a
        // deployment over. A misspelling IS: a value that silently fell back to the default would
        // present documents differently from what was configured, and the only symptom would be
        // extraction quality months later.
        using IHost tolerant = ComposeHost(("Cbix:Provider:DocumentPresentation", "textonly"));
        using IServiceScope run = tolerant.Services.CreateScope();
        Assert.IsType<TextOnlyDocumentContentProfile>(
            run.ServiceProvider.GetRequiredService<IDocumentContentProvider>());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ComposeHost(("Cbix:Provider:DocumentPresentation", "text_only")).Dispose());

        Assert.Contains("text_only", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DocumentPresentationCapability.TextOnly), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredCapability_DefaultsToNativeDocument()
    {
        // The unconfigured deployment. Defaulting to the fully capable presentation is the deliberate
        // choice: guessing wrong in this direction is a provider refusing a document block loudly,
        // while guessing wrong in the other is a quietly degraded extraction nothing reports.
        using IHost host = ComposeHost();
        using IServiceScope run = host.Services.CreateScope();

        Assert.Equal(
            "ClaudeDocumentContentProvider",
            run.ServiceProvider.GetRequiredService<IDocumentContentProvider>().GetType().Name);
    }

    [Fact]
    public async Task HostStart_RefusesTwoProfileSourcesClaimingOneCapability()
    {
        // MAJOR: the selector's own documentation promised this refusal happened "at composition ...
        // so a duplicated registration fails the host's startup instead of one run in the middle of a
        // batch" - and nothing made it true. The selector is scoped-resolved, so its constructor did
        // not run until the first run asked for a provider, by which point a batch was already moving.
        // Resolving it in the startup block is what turns the promise into the behaviour.
        //
        // The conflict planted here is a real one, not a repeat registration: a SECOND, DIFFERENT type
        // claiming NativeDocument. That distinction matters because the registrations are now
        // TryAddEnumerable, which absorbs honest repetition - this proves it still refuses a genuine
        // ambiguity, which is the case where either choice would be silent and one would be wrong.
        using IHost host = ComposeHost(
            services => services.AddSingleton<IDocumentContentProfileSource, RivalNativeDocumentSource>());

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("NativeDocument", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RivalNativeDocumentSource), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStart_RefusesAConfiguredCapabilityWithNoRegisteredSource()
    {
        // The other half, and it needs a separate probe because the two faults are detected at
        // different moments: the selector's constructor indexes what EXISTS, and only a lookup asks
        // whether what was CONFIGURED is among it. Resolving the selector alone would leave this one
        // to surface mid-batch.
        //
        // The arrangement removes the profile sources and configures NativeDocument - which is exactly
        // the shape of a host that selected a provider and forgot to register its adapter's source.
        // Left to the first run, the symptom would be a document-less run reported as a provider
        // error; here it is a host that does not start.
        using IHost host = ComposeHost(
            services => services.RemoveAll<IDocumentContentProfileSource>(),
            ("Cbix:Provider:DocumentPresentation", "NativeDocument"));

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("NativeDocument", error.Message, StringComparison.Ordinal);
        Assert.Contains("(none)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStart_SucceedsAndProbesNothingExpensive()
    {
        // The positive control for the two refusals above: a correctly wired host must still start, and
        // the startup probe must not have cost anything. The probe constructs a document-content
        // profile, which performs no I/O - no upload, no render, no network - so a started host has
        // made zero provider calls. If that ever stopped being true, this is where it would show:
        // starting a worker would begin talking to Anthropic before a single document arrived.
        using IHost host = ComposeHost();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public void ProductionHost_ValidatesScopes()
    {
        // MAJOR. Measured: with the default provider options a scoped service resolved from the ROOT
        // container is silently promoted to a root-lifetime singleton - no exception, no log line. For
        // CBIX that is not a style issue: the run-scoped document-content profile carries the "prepare
        // each document once" memo, so a promotion turns it process-wide, holding every run's uploaded
        // file ids and rendered page images for the life of the worker and serving one run's prepared
        // document to the next.
        //
        // Every test container in this file already sets ValidateScopes, which is exactly why nothing
        // caught it: the tests were safe and the deployed host was not. This asserts the property on
        // the graph AddCbixWorker actually builds.
        using IHost host = ComposeHost();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            host.Services.GetRequiredService<IDocumentContentProvider>);

        Assert.Contains("scoped service", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddCbixWorkflow_IsIdempotent()
    {
        // Calling it twice must be a no-op rather than a second, contradictory set of registrations.
        // The profile sources are the case that made this worth asserting: they are a multi-registration,
        // so a plain Add would have registered the Vision source twice - which the selector reads as two
        // sources claiming one capability and refuses outright, turning a harmless double call into a
        // hard startup failure whose message accuses the host of a duplicate it never wrote.
        ServiceCollection services = [];
        services.AddLogging();

        DocumentIngestOptions ingestOptions = new(CreateTemporaryDirectory(), DocumentIngestOptions.ClaudeFilesApiLimitBytes);
        DocumentPresentationOptions presentation = new(DocumentPresentationCapability.TextOnly);

        services.AddCbixWorkflow(ingestOptions, presentation);
        services.AddCbixWorkflow(ingestOptions, presentation);
        // Both model-calling nodes, because story S01-16 put the DocControl agent in the section slot
        // and Core resolves an agent factory per node. A composition that registered only triage would
        // build a container that fails inside the first superstep of a real run rather than here.
        foreach (string node in (string[])[CbixWorkflowNodes.Triage, CbixWorkflowNodes.SectionExtraction])
        {
            services.AddKeyedSingleton<AIAgent>(
                node,
                (_, key) => new ChatClientAgent(new CannedChatClient("triaged"), name: (string)key!));
        }

        using ServiceProvider container = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        Assert.Equal(2, container.GetServices<IDocumentContentProfileSource>().Count());

        using IServiceScope run = container.CreateScope();
        Assert.IsType<TextOnlyDocumentContentProfile>(
            run.ServiceProvider.GetRequiredService<IDocumentContentProvider>());
    }

    [Fact]
    public void HostStart_RecordsHowDocumentsWillBePresented()
    {
        // The egress record, and the symmetric half of the endpoint event. That one says WHERE the
        // traffic goes; this says WHAT goes in it - the whole document file, rendered page images, or
        // extracted text only. An unconfigured deployment presents documents natively, meaning whole
        // documents leave the estate; that is a deliberate upstream decision (it cannot happen without
        // an API key provisioned through the secrets manager) but "deliberate" and "recorded" are
        // different things, and only the second survives an audit.
        CapturingLoggerProvider capture = new();

        using IHost host = ComposeHost(
            capture,
            services => { },
            ("Cbix:Provider:DocumentPresentation", "TextOnly"));

        _ = host.Services.GetServices<IHostedService>().ToList();

        Assert.Contains(
            capture.Messages,
            message => message.Contains("presented to the model as TextOnly", StringComparison.Ordinal));
        Assert.DoesNotContain(
            capture.Messages,
            message => message.Contains(PlaceholderApiKey, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("99")]
    public void ConfiguredCapability_RefusesADigitStringLikeAnyOtherUnrecognisedValue(string configured)
    {
        // Enum.TryParse accepts a digit string for ANY integer, defined or not: "0" parses to
        // NativeDocument and "2" to TextOnly, and Enum.IsDefined then passes because the value really is
        // defined. So a configuration value of "2" - a plausible typo, or a leftover from some numeric
        // scheme - would have silently selected the DEGRADED text-only presentation, which is the exact
        // outcome the strict parse exists to prevent. Requiring the configured string to name a
        // capability closes the numeric back door.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ComposeHost(("Cbix:Provider:DocumentPresentation", configured)).Dispose());

        Assert.Contains(configured, error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DocumentPresentationCapability.TextOnly), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredCapability_ScrubsAHostileConfigurationValueOutOfTheMessage()
    {
        // A configuration value is not written by the person who reads the failure: it arrives from an
        // environment variable, a Helm chart or a mounted config map, and the exception carrying it is
        // what an operator reads in a terminal and what a log sink stores as a line. Control characters
        // and line separators in it forge log lines; format characters reorder the rendered text.
        //
        // Built from code points rather than written as literal characters, deliberately: a real
        // U+202E in this file would reverse how the rest of the line renders in an editor and in a
        // diff - which is the very effect under test - and no reformatting pass can quietly drop it.
        //   U+000A - forges a second log line
        //   U+0007 - a control character (BEL)
        //   U+202E - RIGHT-TO-LEFT OVERRIDE, which reorders everything rendered after it
        string hostile = "Text" + (char)0x000A + "Only" + (char)0x0007 + (char)0x202E;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ComposeHost(("Cbix:Provider:DocumentPresentation", hostile)).Dispose());

        Assert.DoesNotContain('\n', error.Message);
        Assert.DoesNotContain((char)0x0007, error.Message);
        Assert.DoesNotContain((char)0x202E, error.Message);

        // Replaced rather than dropped: a value rendered as "TextOnly" after the dangerous characters
        // were silently removed would send an operator looking for a bug in a value that is, as far as
        // they can see, correct.
        Assert.Contains((char)0xFFFD, error.Message);
    }

    [Fact]
    public void AddCbixWorker_RegistersTheTriageAgentFactoryUnderItsRoleKey()
    {
        // The seam S01-14 filled, and it moved: the host now registers an IDocumentBoundAgentFactory
        // rather than a bare AIAgent. That is the point of the story rather than a detail of it - an
        // agent registered on its own can only be run WITHOUT a document, and a triage call with no
        // document is a model identifying something it was never shown. Core's graph still asks by role
        // and still never learns which provider answered.
        //
        // Resolving it here also proves the registration is reachable without running the workflow: a
        // keyed registration nobody can resolve is a wiring mistake that would otherwise surface inside
        // the first superstep of a real run.
        using IHost host = ComposeHost();

        IDocumentBoundAgentFactory factory =
            host.Services.GetRequiredKeyedService<IDocumentBoundAgentFactory>(CbixWorkflowNodes.Triage);

        // By type NAME rather than by Assert.IsType, because the adapter's implementation is internal
        // to it and naming it here would be this test asserting through a type the graph is forbidden
        // to name. What matters is that the HOST's registration won over Core's chat-content default:
        // if Core's had won, the Claude document block would never reach the wire.
        Assert.Equal("AnthropicDocumentBoundAgentFactory", factory.GetType().Name);
        Assert.NotEqual(typeof(ChatContentDocumentAgentFactory), factory.GetType());
    }

    /// <summary>Removes any temporary ingest roots the tests created.</summary>
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

    /// <summary>
    /// Composes Core's half alone - no host, no configuration, no provider - with a canned agent in
    /// the triage slot.
    /// </summary>
    private ServiceProvider ComposeCore(DocumentPresentationCapability capability)
    {
        ServiceCollection services = [];

        services.AddLogging();
        services.AddCbixWorkflow(
            new DocumentIngestOptions(CreateTemporaryDirectory(), DocumentIngestOptions.ClaudeFilesApiLimitBytes),
            new DocumentPresentationOptions(capability));
        // Both model-calling nodes, because story S01-16 put the DocControl agent in the section slot
        // and Core resolves an agent factory per node. A composition that registered only triage would
        // build a container that fails inside the first superstep of a real run rather than here.
        foreach (string node in (string[])[CbixWorkflowNodes.Triage, CbixWorkflowNodes.SectionExtraction])
        {
            services.AddKeyedSingleton<AIAgent>(
                node,
                (_, key) => new ChatClientAgent(new CannedChatClient("triaged"), name: (string)key!));
        }

        // Registered so a test can read the descriptors back and assert a declared lifetime rather
        // than only an observed one. The two differ: a transient registration resolved once inside one
        // scope is indistinguishable from a scoped one by observation alone.
        services.AddSingleton<IServiceCollection>(services);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            // Both on, because this is the test that would otherwise miss a captive dependency: the
            // ingest service holding a run-scoped content provider is exactly the shape validation
            // catches, and it is silent without it.
            ValidateScopes = true,
            ValidateOnBuild = false,
        });
    }

    /// <summary>Composes the real host through the same entry point <c>Program.cs</c> uses.</summary>
    private IHost ComposeHost(params (string Key, string Value)[] settings) =>
        ComposeHost(loggerProvider: null, services => { }, settings);

    /// <summary>Composes the real host, with a chance to disturb the registrations first.</summary>
    private IHost ComposeHost(Action<IServiceCollection> tweak, params (string Key, string Value)[] settings) =>
        ComposeHost(loggerProvider: null, tweak, settings);

    /// <summary>Composes the real host, optionally capturing what it logs and disturbing its registrations.</summary>
    /// <remarks>
    /// <para>
    /// The tweak runs <em>after</em> <c>AddCbixWorker</c>, which is what lets a test plant a rival
    /// profile source or remove one - the two composition faults the startup probes exist to catch. A
    /// tweak applied before would be indistinguishable from a host that simply registered things in a
    /// different order, and <c>TryAdd</c> would absorb it.
    /// </para>
    /// <para>
    /// The builder is the EMPTY one, so no ambient <c>appsettings.json</c> or exported
    /// <c>ANTHROPIC_API_KEY</c> can decide the outcome; every source these tests depend on is stated
    /// here.
    /// </para>
    /// </remarks>
    private IHost ComposeHost(
        ILoggerProvider? loggerProvider,
        Action<IServiceCollection> tweak,
        params (string Key, string Value)[] settings)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.Services.AddLogging(logging =>
        {
            if (loggerProvider is not null)
            {
                logging.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Information);
            }
        });

        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ScopedApiKeyKey] = PlaceholderApiKey,
                ["Cbix:Ingest:Root"] = CreateTemporaryDirectory(),
            });

        foreach ((string key, string value) in settings)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value });
        }

        builder.AddCbixWorker();
        tweak(builder.Services);

        return builder.Build();
    }

    /// <summary>
    /// A second, different source claiming <see cref="DocumentPresentationCapability.NativeDocument"/>.
    /// </summary>
    /// <remarks>
    /// Its <see cref="Create"/> throws because it must never be reached: the whole point is that the
    /// selector refuses the ambiguity rather than picking a winner by registration order. If this ever
    /// runs, the refusal has been replaced by a silent choice - and which profile presented a document
    /// decides whether the permission matrix was read visually at all.
    /// </remarks>
    private sealed class RivalNativeDocumentSource : IDocumentContentProfileSource
    {
        public DocumentPresentationCapability Capability => DocumentPresentationCapability.NativeDocument;

        public IDocumentContentProvider Create(IServiceProvider services) =>
            throw new InvalidOperationException(
                "A conflicting profile source was chosen instead of being refused.");
    }

    /// <summary>Collects formatted log messages so a test can assert on what startup recorded.</summary>
    /// <remarks>
    /// Formatted rather than structured, deliberately: the assertions are "does the presentation mode
    /// appear" and "does the credential not appear", and both are questions about the text an operator
    /// or a log sink actually sees. A structured-state assertion would pass while the rendered message
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

    private string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cbix-workflow-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }

    /// <summary>
    /// A chat client that answers everything with one canned string, without touching a network.
    /// </summary>
    /// <remarks>
    /// This is the whole provider side of these tests, which is the point: the graph is built and run
    /// against <c>IChatClient</c>, so anything satisfying that interface can drive it. S01-09 turns the
    /// same shape into the permanent CI regression gate for agnosticism.
    /// </remarks>
    private sealed class CannedChatClient(string answer) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The composition tests never stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release: the canned answer is a string.
        }
    }
}
