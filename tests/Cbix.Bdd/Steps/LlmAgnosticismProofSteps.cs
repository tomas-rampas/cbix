using System.Reflection;
using System.Text.Json;

using Cbix.Agnosticism;
using Cbix.Bdd.Support;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/LlmAgnosticismProof.feature</c> (story S01-09).
/// </summary>
/// <remarks>
/// <para>
/// The run is the production one: <c>Cbix.Core</c>'s <c>AddCbixWorkflow</c> composes it, the real
/// ingest gate reads the committed DE specimen off disk, PDFPig builds the real text layer, and MAF
/// executes the real graph. Exactly one thing is substituted - the model - and it is substituted at
/// the seam the host fills in production, so the swap is a registration rather than a fork of the
/// pipeline.
/// </para>
/// <para>
/// <b>The two assertions are of different kinds and both are needed.</b> "The run completes to the
/// persist step" is behavioural: it says the pipeline works with a non-Anthropic provider. "No
/// assembly reachable from this run's dependency graph is Microsoft.Agents.AI.Anthropic" is
/// structural: it says the pipeline could not have used one even if it had wanted to. Story S01-09's
/// "Done means" requires the second explicitly - "a passing run alone is not sufficient evidence" -
/// because a green run proves only that the provider was not needed on this path today, not that it
/// is absent from the graph.
/// </para>
/// </remarks>
[Binding]
public sealed class LlmAgnosticismProofSteps(LlmAgnosticismProofState state)
{
    /// <summary>
    /// What the stub answers triage with: a structured-output payload in the shape of design
    /// Appendix A's <c>DocumentProfile</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shaped for the executor that exists today, not for the one story S01-14 will write.</b>
    /// <see cref="TriageExecutor"/> currently carries the agent's answer through as text and parses
    /// nothing; <c>DocumentProfile</c> parsing, its schema and its validation are S01-14's, and
    /// building them here would be that story done in the wrong place and without its scenarios.
    /// </para>
    /// <para>
    /// It is still JSON rather than prose, and that is the story's own wording - "canned
    /// structured-output JSON". The agnosticism-relevant property is that a structured payload
    /// survives the neutral path (<c>IChatClient</c> to <c>ChatClientAgent</c> to
    /// <c>AgentResponse.Text</c>) byte for byte, with no provider-specific handling anywhere in it.
    /// That is what the assertions below check, and it is what will still have to be true when
    /// S01-14 starts deserialising this shape.
    /// </para>
    /// </remarks>
    private const string CannedTriageProfileJson =
        """{"DocType":"Cross-Border Trading Legal Instruction","JurisdictionIso":"DE","DocRef":"CBTI-DE-2024","Version":"4.2","LayoutFamily":"contoso-country-manual-v4","Confidence":0.97}""";

    /// <summary>The fields design Appendix A's <c>DocumentProfile</c> declares, in its order.</summary>
    private static readonly string[] DocumentProfileFields =
        ["DocType", "JurisdictionIso", "DocRef", "Version", "LayoutFamily", "Confidence"];

    /// <summary>
    /// Assemblies whose presence in the closure proves the walk covered the pipeline rather than
    /// some smaller graph that happened to be provider-free.
    /// </summary>
    private static readonly string[] PipelineAssemblies =
    [
        "Cbix.Core",
        "Microsoft.Agents.AI.Workflows",
        "Microsoft.Agents.AI.Abstractions",
        "UglyToad.PdfPig",
    ];

    /// <summary>The assembly the control scenario roots its walk at: the production host.</summary>
    /// <remarks>
    /// Loaded by name rather than named as a type on purpose. Referencing <c>Cbix.Worker</c> from a
    /// step binding would emit a compile-time reference and make a missing host a build break; here
    /// it must be a failing assertion, and the load is asserted so the control can never pass by
    /// having walked nothing.
    /// </remarks>
    private const string WorkerHostAssemblyName = "Cbix.Worker";

    /// <summary>The adapter the control expects to find on the path from the host to the provider.</summary>
    private const string ProviderAdapterAssemblyName = "Cbix.Providers.Anthropic";

    /// <summary>The provider integration the story names.</summary>
    private const string ProviderIntegrationAssemblyName = "Microsoft.Agents.AI.Anthropic";

    [Given("a stub \"IChatClient\" implementation that returns canned structured-output JSON")]
    public void GivenAStubChatClientThatReturnsCannedStructuredOutputJson()
    {
        // The premise, asserted rather than described: a scenario that claimed structured output and
        // handed the pipeline prose would still go green on every assertion below it, because
        // nothing downstream parses the answer yet. This is what stops the word "JSON" in the
        // Gherkin from being decoration.
        using JsonDocument profile = JsonDocument.Parse(CannedTriageProfileJson);

        foreach (string field in DocumentProfileFields)
        {
            Assert.True(
                profile.RootElement.TryGetProperty(field, out _),
                $"The canned triage answer is missing '{field}', so it is not the DocumentProfile shape "
                    + "design Appendix A declares.");
        }

        state.ChatClient = new StubChatClient(CannedTriageProfileJson);
    }

    [Given("the workflow is configured with the stub client instead of the Anthropic adapter")]
    public void GivenTheWorkflowIsConfiguredWithTheStubClient()
    {
        ServiceCollection services = [];

        // The caller's half of the composition root, and the only part not delegated to
        // Cbix.Agnosticism: logging sinks are a host decision in production too, and keeping the
        // implementation package out of the stub assembly keeps that assembly's own reference table
        // - which is evidence in this story - as small as it can be.
        services.AddLogging();
        services.AddStubBackedCbixWorkflow(RepositoryLayout.DataDirectory(), ChatClient);

        // ValidateScopes, matching the production host: a scoped service captured by a singleton is
        // the defect this composition is most exposed to (the document-content profile is
        // run-scoped), and it is silent without this.
        ServiceProvider container = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        state.Services = services;
        state.Container = container;
        state.RunScope = container.CreateScope();
        state.DocumentPath = RepositoryLayout.DeSpecimenPath();

        // "Instead of the Anthropic adapter", made checkable at the seam itself. The structural
        // assertion later covers this and much more; this one is here so a mis-wired triage slot
        // fails while the composition is still on screen rather than inside a closure walk.
        AIAgent agent = RunScope.ServiceProvider.GetRequiredKeyedService<AIAgent>(CbixWorkflowNodes.Triage);

        Assert.False(
            IsProviderAssembly(agent.GetType().Assembly.GetName().Name ?? string.Empty),
            $"The triage slot resolved a provider-backed agent ({agent.GetType().FullName}); the "
                + "scenario would be proving agnosticism against the provider it excludes.");
    }

    [When("the workflow runs against a test document")]
    public async Task WhenTheWorkflowRunsAgainstATestDocument()
    {
        Workflow workflow = RunScope.ServiceProvider.GetRequiredService<CbixWorkflowFactory>().Build();

        StreamingRun run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new DocumentSubmission(state.DocumentPath!));

        await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync())
        {
            state.Events.Add(workflowEvent);
        }

        // Kept because the graph is half the root set for the structural assertion: the executors
        // MAF bound in are read off this object rather than off a list somebody maintains.
        state.Workflow = workflow;
    }

    [Then("the run completes to the persist step")]
    public void ThenTheRunCompletesToThePersistStep()
    {
        // The workflow's own terminal signal, not merely the last executor to have run. An event
        // stream that simply stopped would satisfy a last-executor check while being
        // indistinguishable from a stall - and "it stalled" is exactly what a broken provider
        // substitution would look like.
        WorkflowOutputEvent output = Assert.Single(state.Events.OfType<WorkflowOutputEvent>());
        ExtractionRunOutcome outcome = Assert.IsType<ExtractionRunOutcome>(output.Data);

        Assert.Equal(CbixWorkflowNodes.Persist, output.ExecutorId);
        Assert.Equal(ExtractionRunDisposition.Persisted, outcome.Disposition);
        Assert.EndsWith(
            RepositoryLayout.DeSpecimenFileName,
            outcome.Document.FileName,
            StringComparison.Ordinal);

        List<string> invoked = [.. state.Events.OfType<ExecutorInvokedEvent>().Select(e => e.ExecutorId)];

        Assert.Equal(
            [
                CbixWorkflowNodes.Ingest,
                CbixWorkflowNodes.Triage,
                CbixWorkflowNodes.SectionExtraction,
                CbixWorkflowNodes.Persist,
            ],
            invoked);

        // The model was actually consulted, exactly once. Without this the scenario would pass for a
        // pipeline that had quietly stopped calling the agent at all - which would make the
        // agnosticism claim trivially true and completely worthless.
        Assert.Equal(1, ChatClient.RequestCount);

        // The structured payload survived the neutral path byte for byte. This is the property
        // S01-14 will build its parsing on, and it is provider-independent by construction: nothing
        // between the stub and here is allowed to know what a provider is.
        ExecutorCompletedEvent triaged = Assert.Single(
            state.Events.OfType<ExecutorCompletedEvent>(),
            completed => string.Equals(completed.ExecutorId, CbixWorkflowNodes.Triage, StringComparison.Ordinal));

        Assert.Equal(CannedTriageProfileJson, Assert.IsType<TriagedDocument>(triaged.Data).AgentResponse);

        Assert.Empty(state.Events.OfType<WorkflowWarningEvent>());
        Assert.Empty(state.Events.OfType<WorkflowErrorEvent>());
    }

    [Then("no assembly reachable from this run's dependency graph is \"Microsoft.Agents.AI.Anthropic\"")]
    public void ThenNoAssemblyReachableFromThisRunsDependencyGraphIsTheProviderIntegration()
    {
        IReadOnlyList<Assembly> roots = WorkflowRunDependencyGraph.Roots(
            state.Services!,
            RunScope.ServiceProvider,
            state.Workflow ?? throw new InvalidOperationException("The run must execute before its graph can be walked."),
            ChatClient);

        state.WalkRoots = roots;
        state.Reachability = AssemblyReachability.From(roots);

        // THE GUARDS RUN FIRST, DELIBERATELY. The ban assertion below passes over an empty offender
        // list, and an empty offender list is what a collapsed walk produces too - so the guards are
        // not decoration after the fact, they are the precondition that makes the ban assertion mean
        // anything. Ordering them first also makes the invariant survive a future Gherkin refactor
        // that extracts the ban into its own Then step: whatever runs first, the walk has already
        // been proved non-vacuous.
        AssertTheWalkSawSomethingMeaningful(Reachability);

        // The root set really was this pipeline's. A walk handed the wrong scope, or a graph that had
        // silently lost its executors, would report clean while proving nothing at all - and each name
        // here is a part whose absence would mean exactly that: the assembly declaring the graph, the
        // workflow engine, the neutral agent currency, and the ingest stack's PDF reader.
        foreach (string expected in PipelineAssemblies)
        {
            Assert.True(
                Reachability.DistanceTo(expected) is not null,
                $"'{expected}' is not reachable from the run's graph, so the walk did not cover the pipeline.");
        }

        // The stub is a ROOT, not merely something reachable: the run executes it, so its own
        // reference table is inside the claim rather than adjacent to it. This is the assertion that
        // would fail if the stub were ever moved back into a test project that references an adapter.
        Assert.Equal(0, Reachability.DistanceTo(typeof(StubChatClient).Assembly.GetName().Name!));

        IReadOnlyList<string> offenders = Reachability.Describe(IsProviderAssembly);

        Assert.True(
            offenders.Count == 0,
            $"A provider assembly is reachable from this run's dependency graph: {string.Join(", ", offenders)}. "
                + $"The walk started at {WalkRoots.Count} roots and reached {Reachability.Reached.Count} "
                + "assemblies. Design 3 makes this run the acceptance criterion for LLM-agnosticism, so a "
                + "provider on the path means the pipeline can no longer be run without one.");
    }

    [Given("the same reachability walk rooted at the worker host, which does choose a provider")]
    public void GivenTheSameWalkRootedAtTheWorkerHost()
    {
        Assembly host = Assembly.Load(new AssemblyName(WorkerHostAssemblyName));

        // Asserted rather than assumed. A control rooted at an assembly that is not the host - a
        // renamed project, a stale copy, a resolver returning something else - would go on to fail
        // with "the provider is not reachable", which reads as a defect in the walk rather than in
        // the control's own premise.
        Assert.Equal(WorkerHostAssemblyName, host.GetName().Name);

        state.WalkRoots = [host];
    }

    [When("the reachability walk runs")]
    public void WhenTheReachabilityWalkRuns() =>
        state.Reachability = AssemblyReachability.From(
            state.WalkRoots ?? throw new InvalidOperationException("No roots were chosen; a Given step must run first."));

    [Then("\"Microsoft.Agents.AI.Anthropic\" is reported as reachable")]
    public void ThenTheProviderIntegrationIsReportedAsReachable()
    {
        // The control. The assertion in the first scenario passes over an empty offender list, which
        // is indistinguishable from a walk that finds nothing because it is broken - a bad root set,
        // a reference table it cannot read, an exception swallowed on load. This proves the same
        // walk, unchanged, reports the banned assembly when one really is reachable.
        Assert.NotNull(Reachability.DistanceTo(ProviderIntegrationAssemblyName));

        Assert.NotEmpty(Reachability.Describe(IsProviderAssembly));
    }

    [Then("it was reached through the provider adapter rather than as a direct reference")]
    public void ThenItWasReachedThroughTheProviderAdapter()
    {
        // Transitivity, which is the property the whole instrument depends on and the one a
        // one-hop check would not have. The host emits no reference to the provider integration at
        // all - containment forbids it naming an Anthropic type, and ProviderContainmentTests
        // enforces that - so the only way to see it from here is through the adapter it does
        // reference. Distance 2 is that path, stated as a number rather than as a hope.
        Assert.Equal(1, Reachability.DistanceTo(ProviderAdapterAssemblyName));
        Assert.Equal(2, Reachability.DistanceTo(ProviderIntegrationAssemblyName));

        AssertTheWalkSawSomethingMeaningful(Reachability);
    }

    /// <summary>Fails a walk that reported clean because it barely walked.</summary>
    /// <remarks>
    /// Applied to both scenarios, so the control is held to the same standard as the proof. Every
    /// clause here is a way a reachability assertion can be vacuous while looking green: too few
    /// assemblies, a root set missing the pipeline's own parts, a walk that never went past the
    /// roots' direct references, or references it silently could not follow.
    /// </remarks>
    private static void AssertTheWalkSawSomethingMeaningful(AssemblyReachabilityResult reachability)
    {
        // Measured 2026-08-17 on net10.0, per scenario rather than in aggregate: the RUN's graph
        // closes over 102 assemblies from 9 roots; the CONTROL, rooted at the single Cbix.Worker
        // assembly, closes over 131 - larger, because a host drags in the configuration, hosting and
        // provider stacks the pipeline itself does not. (An earlier version of this comment had the
        // two transposed, which is what a stamped number is for: it is checkable, and it was checked.)
        // The floor is deliberately far below both - it catches a walk that collapsed, not a number
        // that moves with every SDK servicing release - and it was verified to bite by raising it and
        // watching both scenarios fail with their real counts.
        Assert.True(
            reachability.Reached.Count >= 30,
            $"The walk reached only {reachability.Reached.Count} assemblies, which is too few to be the "
                + "closure of a CBIX run; the root set or the walk is broken.");

        int deepest = reachability.Reached.Max(name => reachability.DistanceTo(name) ?? 0);

        Assert.True(
            deepest >= 2,
            $"Nothing was reached beyond a root's direct references (deepest distance {deepest}), so the "
                + "walk is not transitive and would miss a provider one hop further out - which is the only "
                + "way this ever goes wrong in practice.");

        // Fail-closed, and kept strict rather than softened to "no unexplored name is a provider": an
        // assembly that cannot be loaded has its OWN references unwalked, so a provider could sit
        // behind it unseen. Measured empty on Windows; if this ever fires only on the Linux CI runner
        // it is naming a real platform difference in the graph, and the fix is to look at the named
        // assembly rather than to relax the assertion.
        Assert.True(
            reachability.Unexplored.Count == 0,
            "Some referenced assemblies could not be loaded, so part of the graph went unwalked: "
                + $"{string.Join(", ", reachability.Unexplored.Order(StringComparer.Ordinal))}.");
    }

    /// <summary>
    /// Whether an assembly name belongs to a model provider or to one of this solution's provider
    /// adapters.
    /// </summary>
    /// <remarks>
    /// Ordinal-contains on the vendor token rather than equality on two names, matching
    /// <c>CoreAssemblyNeutralityTests</c>' reasoning: it has to catch <c>Anthropic</c>,
    /// <c>Microsoft.Agents.AI.Anthropic</c> and any future <c>Anthropic.Something</c> without a list
    /// being kept current. The <c>Cbix.Providers.</c> prefix is banned alongside it because an
    /// adapter reaching the run's graph is the same defect one hop earlier.
    /// </remarks>
    private static bool IsProviderAssembly(string assemblyName) =>
        assemblyName.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)
        || assemblyName.StartsWith("Cbix.Providers.", StringComparison.Ordinal);

    /// <summary>The run scope, or a failure naming the step that should have created it.</summary>
    private IServiceScope RunScope =>
        state.RunScope ?? throw new InvalidOperationException(
            "No workflow was composed; a Given step must run before this one.");

    /// <summary>The stub chat client, or a failure naming the step that should have created it.</summary>
    private StubChatClient ChatClient =>
        state.ChatClient ?? throw new InvalidOperationException(
            "No stub chat client was created; a Given step must run before this one.");

    /// <summary>The roots the walk started from, or a failure naming the step that should have chosen them.</summary>
    private IReadOnlyList<Assembly> WalkRoots =>
        state.WalkRoots ?? throw new InvalidOperationException(
            "No roots were chosen; a Given or Then step must set them before this one.");

    /// <summary>What the walk found, or a failure naming the step that should have run it.</summary>
    private AssemblyReachabilityResult Reachability =>
        state.Reachability ?? throw new InvalidOperationException(
            "The reachability walk has not run; a When step must run before this one.");
}
