using System.Reflection;

using Cbix.Core.Documents;
using Cbix.Core.Ingest;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Cbix.Bdd.Support;

/// <summary>
/// Derives the root set for story S01-09's reachability walk: the assemblies whose code one
/// workflow run actually executes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The definition, stated precisely, because it is the difference between a gate and theatre.</b>
/// The story's assertion is about <em>this run's dependency graph</em>, not about the test project's
/// references. <c>Cbix.Bdd</c> references <c>Cbix.Providers.Anthropic</c> permanently - story
/// S01-08's scenarios test the adapter - so "the test assembly references no provider" is a claim
/// that is false today and is meant to remain false. What is walked instead is the union of:
/// </para>
/// <list type="number">
///   <item><description>the assembly that declares the graph (<c>Cbix.Core</c>);</description></item>
///   <item><description>the declaring assembly of every executor MAF bound into that graph, read off
///   the built <see cref="Workflow"/> rather than off a list somebody maintains;</description></item>
///   <item><description>every service registration the run was composed from - implementation type,
///   pre-built instance, or the type declaring the factory delegate, plus the service type
///   itself;</description></item>
///   <item><description>the concrete types the run actually resolved where a registration cannot
///   show them: the container, the triage <see cref="AIAgent"/>, the
///   <see cref="IChatClient"/> behind it, the document-content profile and the ingest
///   service.</description></item>
/// </list>
/// <para>
/// <b>What is deliberately NOT a root, and why that is principled rather than convenient.</b> The
/// driver - the step binding that calls <c>InProcessExecution</c>, and the xUnit/Reqnroll/VSTest
/// machinery under it - is excluded. In production the driver is the worker host, and a host is
/// precisely the thing that <em>chooses</em> a provider: <c>Cbix.Worker</c> references the adapter
/// because that is its job. Including the caller would therefore make the property unachievable by
/// construction and so meaningless. The claim is about the pipeline, not about whoever started it.
/// </para>
/// <para>
/// That exclusion is what forces the stub chat client into its own assembly. The stub is not the
/// driver: the run executes it, so it is a root, and it is walked like everything else. Had it been
/// declared in <c>Cbix.Bdd</c> the walk would have had to skip a root it genuinely executes, which
/// is the exclusion-by-fiat this design refuses. The control scenario demonstrates that the walk
/// detects a provider by rooting it at <c>Cbix.Worker</c> - the production host, which chooses one
/// deliberately - and requiring the provider to be found there. <c>Cbix.Bdd</c> would be the wrong
/// control: it project-references the adapter but emits no reference to it (its bindings resolve
/// adapter types reflectively), so a walk rooted there finds nothing and would prove nothing.
/// </para>
/// <para>
/// <b>What this instrument cannot see, stated where the strongest claim is made.</b> It reads
/// reference tables, so it proves that no assembly on the path <em>names</em> a provider type. It
/// does not - and cannot - see <c>Assembly.Load</c> by string plus <see cref="Activator"/>, a
/// reflective member lookup, or a plugin loaded from configuration: none of those emits a reference.
/// That is the same limitation <c>ProviderContainmentTests</c> records for its own member-reference
/// scan, and the same reasoning applies to accepting it - the accident being guarded against is
/// somebody wiring a provider into the pipeline because it was convenient, and that accident always
/// emits a reference. A deliberate reflective smuggle would defeat this, and defeating it would take
/// code nobody could write by mistake.
/// </para>
/// </remarks>
public static class WorkflowRunDependencyGraph
{
    /// <summary>
    /// Assemblies that must be in every run's root set: the graph, the container that resolved it,
    /// the neutral agent wrapper, and the stub standing in for a provider.
    /// </summary>
    /// <remarks>
    /// A named set rather than only a count, because a count is satisfied by the wrong assemblies.
    /// These four are the ones whose absence changes what the gate means - lose the stub and the
    /// provider side is unwalked; lose the container and the composition is.
    /// </remarks>
    private static readonly string[] RequiredRoots =
    [
        "Cbix.Core",
        "Cbix.Agnosticism",
        "Microsoft.Agents.AI",
        "Microsoft.Extensions.DependencyInjection",
    ];

    /// <summary>Collects the roots for one composed, executed run.</summary>
    /// <param name="services">The collection the run was composed from.</param>
    /// <param name="runScope">The service provider for the run's scope.</param>
    /// <param name="workflow">The graph the run executed.</param>
    /// <param name="chatClient">The chat client the composition was handed.</param>
    /// <returns>The root assemblies, de-duplicated and ordered by name.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IReadOnlyList<Assembly> Roots(
        IServiceCollection services,
        IServiceProvider runScope,
        Workflow workflow,
        IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runScope);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(chatClient);

        HashSet<Assembly> roots =
        [
            // The graph itself.
            typeof(CbixWorkflowFactory).Assembly,

            // The container implementation that resolved every node. Taken from the live object
            // rather than named, so a host that swapped containers would be walked as it is.
            runScope.GetType().Assembly,

            // The provider side of the run, in full: the client, and the neutral MAF adapter that
            // turned it into an agent. These two are the entire substitution point - production
            // replaces them with the Anthropic adapter's output and changes nothing else.
            chatClient.GetType().Assembly,
            runScope.GetRequiredKeyedService<AIAgent>(CbixWorkflowNodes.Triage).GetType().Assembly,

            // Factory-registered services: their implementation type appears in no descriptor, so
            // only the resolved instance can name it. The document-content profile is the one that
            // matters most - it is the port a provider plugs its native-document handling into, so
            // an Anthropic-backed profile resolving here would be exactly the leak this gate is for.
            //
            // TODO (Sprint 02, inherited obligation): this list is HAND-KEPT, and it is the one part
            // of the root set that is. The mitigating property is the same one the executor loop
            // below relies on - a missing entry can only make the walk SMALLER, never let a provider
            // through undetected on a path that is covered - but "smaller" is precisely how a gate
            // rots. Sprint 02 adds six more keyed section agents (Entities, Matrix, Conditions,
            // Marketing, Tax, VersionHistory); each must be added here, or the whole set must be
            // derived - enumerate the keyed AIAgent registrations off the ServiceCollection and
            // resolve each one - which is the better fix and is why this is a TODO rather than a
            // comment.
            runScope.GetRequiredService<IDocumentContentProvider>().GetType().Assembly,
            runScope.GetRequiredService<DocumentIngestService>().GetType().Assembly,
        ];

        // Every executor the runtime will actually invoke, read off the built graph. A hand-kept
        // list would go stale the first time a sprint adds a node - and would go stale silently,
        // because a missing root only ever makes this walk smaller.
        int boundExecutors = 0;
        foreach (ExecutorBinding binding in workflow.ReflectExecutors().Values)
        {
            if (binding.ExecutorType is { } executorType)
            {
                boundExecutors++;
                roots.Add(executorType.Assembly);
            }
        }

        // Fail closed on the loop itself, not just on its result. MAF populates ExecutorType from the
        // binding, and a version that stopped doing so - or a graph built through a factory-only
        // overload - would leave this loop contributing nothing while every assertion downstream
        // still passed, because the executors' assembly happens to be Cbix.Core and is already a root
        // for other reasons. That is a silently shrinking walk, which is the failure mode this whole
        // instrument is built to avoid.
        Assert.True(
            boundExecutors > 0,
            "No executor binding in the built graph exposed an ExecutorType, so the executor half of "
                + "the root set contributed nothing and the walk quietly narrowed.");

        int beforeRegistrations = roots.Count;

        foreach (ServiceDescriptor descriptor in services)
        {
            AddRegistration(descriptor, roots);
        }

        // The descriptor loop must contribute too, asserted as strict growth rather than by naming an
        // assembly: the registrations bring in the logging and options implementations that nothing
        // else in the root set names, and growth is the claim that survives a container or a logging
        // package reorganising which assembly declares what. Deleting the loop, or breaking the
        // keyed/unkeyed branch so it throws nothing and adds nothing, fails here instead of shrinking
        // the walk in silence.
        Assert.True(
            roots.Count > beforeRegistrations,
            $"The service-registration loop added no root ({services.Count} descriptors, {roots.Count} "
                + "roots before and after); the composed collection was empty or the descriptor walk is "
                + "broken.");

        // The named half of the guard, because a count is satisfied by the wrong assemblies. Two of
        // these can only arrive through the live objects the run used - Microsoft.Agents.AI from the
        // resolved agent's concrete type, Cbix.Agnosticism from the client behind it - so a resolution
        // silently returning something else, or that block being trimmed, fails here rather than
        // producing a smaller walk. The other two would also arrive through registrations; they are
        // named anyway because their absence is what would change the gate's meaning.
        foreach (string required in RequiredRoots)
        {
            Assert.True(
                roots.Any(assembly => string.Equals(assembly.GetName().Name, required, StringComparison.Ordinal)),
                $"'{required}' is not in the root set, so the walk no longer starts from the parts of the "
                    + "run it claims to cover.");
        }

        return [.. roots.OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal)];
    }

    /// <summary>Adds whatever assemblies one registration can contribute.</summary>
    /// <remarks>
    /// <para>
    /// All four shapes are taken, not just the implementation: the service type is what the run
    /// depends on by contract, and a factory delegate's declaring type is the assembly whose code
    /// runs at resolution time.
    /// </para>
    /// <para>
    /// <b>What that last one does and does not do</b>, stated as the conditional it actually is. A
    /// lambda registered from the driver puts the driver's assembly into the root set - correct,
    /// because the run then executes driver code at resolution time - but it fails the gate only if
    /// that assembly transitively references a provider. For <c>Cbix.Bdd</c> today it does not: the
    /// project references the adapter, yet the compiler emits no reference to it because the S01-08
    /// bindings resolve adapter types reflectively. So a driver-registered lambda is currently
    /// harmless <em>by accident</em>, and would stop being harmless the moment any binding in this
    /// assembly names an adapter type. That accident is exactly why the stub and its composition live
    /// in <c>Cbix.Agnosticism</c>, whose reference table is provider-free by construction rather than
    /// by coincidence.
    /// </para>
    /// <para>
    /// Keyed and unkeyed descriptors are read through different properties, and reading the wrong
    /// pair throws rather than returning null (measured behaviour of
    /// <see cref="ServiceDescriptor"/>). The triage agent is registered keyed, so this branch is
    /// exercised on every run rather than being defensive padding.
    /// </para>
    /// </remarks>
    private static void AddRegistration(ServiceDescriptor descriptor, HashSet<Assembly> roots)
    {
        roots.Add(descriptor.ServiceType.Assembly);

        if (descriptor.IsKeyedService)
        {
            AddIfPresent(descriptor.KeyedImplementationType, roots);
            AddIfPresent(descriptor.KeyedImplementationInstance?.GetType(), roots);
            AddIfPresent(descriptor.KeyedImplementationFactory?.Method.DeclaringType, roots);
            return;
        }

        AddIfPresent(descriptor.ImplementationType, roots);
        AddIfPresent(descriptor.ImplementationInstance?.GetType(), roots);
        AddIfPresent(descriptor.ImplementationFactory?.Method.DeclaringType, roots);
    }

    private static void AddIfPresent(Type? type, HashSet<Assembly> roots)
    {
        if (type is not null)
        {
            roots.Add(type.Assembly);
        }
    }
}
