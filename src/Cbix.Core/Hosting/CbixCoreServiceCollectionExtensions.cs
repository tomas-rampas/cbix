using Cbix.Core.Agents;
using Cbix.Core.Documents;
using Cbix.Core.Ingest;
using Cbix.Core.Review;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Cbix.Core.Hosting;

/// <summary>
/// Registers the provider-neutral half of the CBIX composition root: the ingest stack, the
/// document-content profiles and the workflow graph.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is choosable without knowing which model provider will be used.</b> That is the
/// line CLAUDE.md draws between this method and the host's <c>AddCbixWorker</c>: the host picks the
/// provider, resolves its credential and registers the resulting <see cref="AIAgent"/> instances and
/// native-document profile source; this method wires the graph those pieces slot into. A test - and
/// S01-09's agnosticism run in particular - calls this, adds a canned agent, and has the real
/// composition with no executable and no provider package anywhere on the path.
/// </para>
/// <para>
/// <b>Lifetimes are the substance of this method, not bookkeeping.</b> Two rules decide every one of
/// them:
/// </para>
/// <list type="number">
///   <item><description>
///   <b>Anything holding per-document state is run-scoped.</b> <see cref="IDocumentContentProvider"/>
///   carries the "prepare each document once" memo; the run scope is its eviction bound, and there is
///   deliberately no process-lifetime cache to size or expire. A singleton registration would hold
///   every run's uploaded file ids - and, for the generic-vision profile, every run's rendered page
///   images - for as long as the process lives. That is not a slow leak: a rendered page is megabytes
///   of pixels, and a worker draining a queue would accumulate them without bound. The scope is
///   asserted by a test, because a lifetime is a one-word mistake with no compile-time symptom.
///   </description></item>
///   <item><description>
///   <b>Anything stateless or process-wide is a singleton.</b> The text-layer extractor, the page
///   renderer, the registry and the audit log all outlive a run by design - the registry is what makes
///   dedupe work <em>across</em> runs, so scoping it would silently disable deduplication.
///   </description></item>
/// </list>
/// <para>
/// The executors and the workflow factory are scoped because they hold the scoped ingest service; the
/// graph is therefore built per run, which is cheap and removes any question of an executor being
/// shared between concurrent runs.
/// </para>
/// </remarks>
public static class CbixCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ingest stack, the local document-content profiles and the workflow graph.
    /// </summary>
    /// <param name="services">The container to register into.</param>
    /// <param name="ingestOptions">
    /// Where documents may be read from and how large one may be. Passed rather than bound, because
    /// binding it would put a configuration dependency into Core for two scalars the host already has.
    /// </param>
    /// <param name="presentationOptions">
    /// The configured provider's document-presentation capability, or <see langword="null"/> for the
    /// default. It selects the active profile; no caller names a profile type.
    /// </param>
    /// <param name="routingOptions">
    /// Where triage's review edge sits, or <see langword="null"/> for the default. Passed rather than
    /// bound, for the same reason as the ingest options: binding it would put a configuration
    /// dependency into Core for one number the host already has.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="ingestOptions"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <b>Every registration is <c>TryAdd</c>-shaped where a host might legitimately substitute.</b>
    /// The registry, the audit log, the text-layer extractor and the page renderer all have in-memory
    /// or local implementations here that Sprint 03 replaces with SQL-backed ones; a host that
    /// registers its own first keeps it. The workflow's own parts are added unconditionally - the
    /// graph is not a substitution point, and a host quietly replacing a node would defeat the reason
    /// the graph lives in Core at all.
    /// </para>
    /// <para>
    /// <b>The triage agent is deliberately not registered here.</b> It is resolved as a keyed
    /// <see cref="IDocumentBoundAgentFactory"/> under <see cref="CbixWorkflowNodes.Triage"/>, and
    /// supplying it is the host's job because constructing one means choosing a provider, a model tier
    /// and a way of getting the document onto the wire. A container missing it fails loudly at the
    /// first resolution with a message naming the key, which is the correct failure for a wiring
    /// mistake.
    /// </para>
    /// <para>
    /// <b>A host that has no provider-specific attachment to make need only register the keyed
    /// <see cref="AIAgent"/>.</b> The default factory below adapts it, which is what lets S01-09's
    /// agnosticism run - and every offline scenario - fill the slot with an <c>IChatClient</c> and
    /// nothing else.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddCbixWorkflow(
        this IServiceCollection services,
        DocumentIngestOptions ingestOptions,
        DocumentPresentationOptions? presentationOptions = null,
        TriageRoutingOptions? routingOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(ingestOptions);

        services.TryAddSingleton(TimeProvider.System);

        // TryAdd, so a second call to this method is a no-op rather than a second, contradictory
        // options object. AddSingleton would leave BOTH in the collection with the last one winning on
        // resolution - so a host that called this twice with different ingest roots would silently run
        // against the second while a reader of the first call believed otherwise. Idempotence here is
        // cheap; a container whose behaviour depends on registration order is not.
        services.TryAddSingleton(ingestOptions);
        services.TryAddSingleton(presentationOptions ?? new DocumentPresentationOptions());
        services.TryAddSingleton(routingOptions ?? new TriageRoutingOptions());

        // Process-wide by design. The registry is what makes a re-submission a duplicate, and dedupe
        // that only worked within one run would not be dedupe at all; the audit log is the trail
        // across runs. Both are in-memory today and become SQL-backed in Sprint 03, at which point the
        // lifetime is unchanged and the storage is not.
        services.TryAddSingleton<IDocumentRegistry, InMemoryDocumentRegistry>();
        services.TryAddSingleton<IIngestAuditLog, InMemoryIngestAuditLog>();

        // Process-wide for the same reason, and more sharply: a queue scoped to the run that filled it
        // would lose every row the moment that run ended, which is the one thing a review queue must
        // not do. In-memory today, SQL-backed in Sprint 03 against db/schema/review_queue.sql; the
        // lifetime does not change when the storage does.
        services.TryAddSingleton<IReviewQueue, InMemoryReviewQueue>();

        // Stateless, and expensive enough to build that per-run construction would be waste: both hold
        // parser and native-renderer setup, neither holds anything about a document between calls.
        services.TryAddSingleton<ITextLayerExtractor, PdfPigTextLayerExtractor>();

        // The render budget - DPI and the page-count, per-page and aggregate megapixel ceilings - is
        // registered as its own defaulted service rather than folded into the renderer. Those numbers
        // bound work done on input an outside party supplies, so a host tuning them for its estate is
        // expected, and a renderer that constructed its own would give it nowhere to do that.
        services.TryAddSingleton(new PageImageRenderOptions());
        services.TryAddSingleton<IPageImageRenderer, PdfiumPageImageRenderer>();

        // The two profiles Core can build without a provider SDK. The native-document source is the
        // host's to register - only an adapter holding a provider client can create it - and a host
        // that configures that capability without registering the source gets a named refusal from the
        // selector rather than a quiet downgrade to a degraded presentation.
        //
        // TryAddEnumerable, not Add, and the distinction matters more here than anywhere else in this
        // method. These are a multi-registration (IEnumerable<IDocumentContentProfileSource>), so a
        // plain Add appends unconditionally - and a second call to this method would register the Vision
        // source twice, which the selector reads as two sources claiming one capability and refuses
        // outright. That turns an idempotent-looking double call into a hard startup failure whose
        // message accuses the host of a duplicate it never wrote. TryAddEnumerable de-duplicates on
        // (service type, implementation type), so honest repetition is absorbed while a GENUINE
        // conflict - two DIFFERENT types claiming the same capability - still reaches the selector and
        // is still refused. That refusal is the one worth keeping.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDocumentContentProfileSource, LocalDocumentContentProfileSources.Vision>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDocumentContentProfileSource, LocalDocumentContentProfileSources.TextOnly>());
        services.TryAddSingleton<DocumentContentProfileSelector>();

        // RUN-SCOPED, and this is the registration the story singles out. The provider holds the
        // per-document memo; a singleton would retain every run's uploads and rendered pixels for the
        // process lifetime, and a transient would re-prepare for every agent in the fan-out and defeat
        // the "upload once" guarantee the port is built around. Registered through a factory so the
        // container owns disposal: the Claude profile is IDisposable behind a port that does not say
        // so, and a scope disposes what it created.
        services.TryAddScoped(provider =>
            provider.GetRequiredService<DocumentContentProfileSelector>().Create(provider));

        // Scoped because it takes the scoped provider above. An ingest service that outlived a run
        // while holding a run-scoped provider is the captive-dependency bug, and it would present as
        // one run's document leaking into another's.
        services.TryAddScoped(provider => new DocumentIngestService(
            provider.GetRequiredService<IDocumentRegistry>(),
            provider.GetRequiredService<IIngestAuditLog>(),
            provider.GetRequiredService<DocumentIngestOptions>(),
            provider.GetRequiredService<ITextLayerExtractor>(),
            provider.GetRequiredService<ILogger<DocumentIngestService>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IDocumentContentProvider>()));

        // TryAdd throughout, for the same idempotence reason as the options above: the graph's nodes are
        // one-of-each services, and a duplicate registration would build a second set of executors that
        // nothing uses while the reader believed there was one.
        // The neutral document-binding seam, and the last TryAdd in this method that a host is
        // EXPECTED to have pre-empted. It adapts whatever keyed AIAgent is registered for the node into
        // one that carries the run's prepared document as ordinary chat content - correct for the
        // text-only and generic-vision profiles, and therefore correct for the whole agnosticism run.
        // A provider whose document block cannot travel as chat content registers its own factory for
        // the same key first (Anthropic's must; see ChatContentDocumentAgentFactory's remarks), and
        // TryAdd is what makes "first registration wins" the rule rather than registration order being
        // a coin toss.
        services.TryAddKeyedScoped<IDocumentBoundAgentFactory>(
            CbixWorkflowNodes.Triage,
            (provider, key) => new ChatContentDocumentAgentFactory(
                provider.GetRequiredKeyedService<AIAgent>(key)));

        services.TryAddScoped<DocumentIngestExecutor>();
        services.TryAddScoped(provider => new TriageExecutor(
            provider.GetRequiredKeyedService<IDocumentBoundAgentFactory>(CbixWorkflowNodes.Triage),
            provider.GetRequiredService<IDocumentContentProvider>(),
            provider.GetRequiredService<ILogger<TriageExecutor>>()));
        services.TryAddScoped<SectionExtractionStubExecutor>();
        services.TryAddScoped<PersistStubExecutor>();
        services.TryAddScoped<DuplicateTerminalExecutor>();
        services.TryAddScoped<ReviewQueueStubExecutor>();
        services.TryAddScoped<CbixWorkflowFactory>();

        return services;
    }
}
