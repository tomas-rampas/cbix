using System.Reflection;

using Cbix.Agnosticism;
using Cbix.Bdd.Support;
using Cbix.Core.Hosting;
using Cbix.Core.Ingest;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/MinimalWorkflowTopology.feature</c> (story S01-13).
/// </summary>
/// <remarks>
/// <para>
/// The graph under test is the production one - built by <c>Cbix.Core</c>'s
/// <see cref="CbixWorkflowFactory"/> out of a container composed by
/// <see cref="CbixCoreServiceCollectionExtensions.AddCbixWorkflow"/> - and it runs against the real DE
/// specimen through the real ingest gate and the real text-only profile. Only the model is canned.
/// That split is the point: everything deterministic is exercised for real, and the one
/// non-deterministic, paid-for component is the one replaced.
/// </para>
/// <para>
/// <b>Ordering is read off the event stream rather than recorded by the executors.</b> MAF raises an
/// <see cref="ExecutorInvokedEvent"/> carrying the executor id, so "ingest first, triage second" is
/// asserted against what the runtime actually scheduled. Executors that appended to a list of their
/// own would be asserting that the test's instrumentation ran in order, which is a different claim.
/// </para>
/// </remarks>
[Binding]
public sealed class MinimalWorkflowTopologySteps(MinimalWorkflowTopologyState state)
{
    /// <summary>The specimen named by the story's Gherkin, relative to the repository's data directory.</summary>
    private const string SpecimenFileName = RepositoryLayout.DeSpecimenFileName;

    /// <summary>
    /// What the canned model answers triage with: a structured-output payload in design Appendix A's
    /// <c>DocumentProfile</c> shape.
    /// </summary>
    /// <remarks>
    /// <b>It was prose until story S01-14, and the change is not cosmetic.</b> Triage now parses its
    /// reply strictly against that contract, so a prose answer is refused and the document is routed to
    /// review instead of to extraction (S01-15). This feature is about the topology's happy path -
    /// ingest, triage, the downstream slot, persist - so its canned reply has to be one triage accepts,
    /// with a confidence above the routing threshold. A scenario left on prose would still have gone
    /// green here and then quietly stopped exercising the spine the moment the routing edges landed.
    /// </remarks>
    private const string CannedTriageAnswer =
        """{"DocType":"Cross-Border Trading Legal Instruction","JurisdictionIso":"DE","DocRef":"CBTI-DE-2024","Version":"4.2","LayoutFamily":"contoso-country-manual-v4","Confidence":0.96}""";

    /// <summary>
    /// How many model calls one complete run makes.
    /// </summary>
    /// <remarks>
    /// Two since story S01-16 put the DocControl agent in the section slot: triage, then the section
    /// agent. Named rather than written as a literal at each call site, because the number is a
    /// property of the topology this feature is about - Sprint 02's fan-out changes it to eight, and it
    /// should change in one place when it does.
    /// </remarks>
    private const int ModelCallsPerRun = 2;

    [Given("the DE specimen submitted to the workflow")]
    public void GivenTheDeSpecimenSubmittedToTheWorkflow() => Compose();

    [Given("the workflow composed from Cbix.Core against a canned chat client")]
    public void GivenTheWorkflowComposedFromCbixCoreAgainstACannedChatClient() => Compose();

    [Given("the DE specimen was already ingested by an earlier run")]
    public async Task GivenTheDeSpecimenWasAlreadyIngestedByAnEarlierRun()
    {
        Compose();

        await RunAsync();

        // The premise, asserted rather than assumed: the second scenario is only about a duplicate if
        // the first submission actually registered something.
        Assert.True(
            RunScope.ServiceProvider.GetRequiredService<IDocumentRegistry>() is InMemoryDocumentRegistry
            {
                Entries.Count: 1,
            },
            "The arranging run did not register the specimen, so the submission under test would not be a duplicate.");

        // The arranging run was a complete run, so it triaged and it called the model. Both are
        // baselined rather than erased: the assertions that follow are about what the SECOND
        // submission did.
        Assert.Equal(ModelCallsPerRun, ChatClient.RequestCount);

        state.ModelCallsBeforeSubmission = ChatClient.RequestCount;
        state.Events.Clear();

        // The arranging run is over, so its scope ends here. One scope per run is the documented shape
        // - it is what bounds the document-content profile's "prepare once" memo - and a scenario that
        // reused one scope for two runs would be testing a shape production never has. It would also
        // hide the very defect the duplicate branch exists to catch: within one scope the memo would
        // still hold the first run's prepared document, so a second submission that wrongly reached a
        // section agent could be served from it and look fine.
        StartNewRunScope();
    }

    [When("the workflow executes")]
    public Task WhenTheWorkflowExecutes() => RunAsync();

    [When("the same document is submitted to the workflow again in a new run")]
    public Task WhenTheSameDocumentIsSubmittedToTheWorkflowAgainInANewRun() => RunAsync();

    [When("I inspect the assemblies the composed graph was built from")]
    public void WhenIInspectTheAssembliesTheComposedGraphWasBuiltFrom()
    {
        // Building it is the inspection's subject: a graph that could not be built without a provider
        // would fail here rather than in the Then step, and would fail for the right reason.
        Workflow workflow = RunScope.ServiceProvider.GetRequiredService<CbixWorkflowFactory>().Build();

        Assert.Equal(CbixWorkflowNodes.Ingest, workflow.StartExecutorId);
    }

    [Then("the ingest executor runs first")]
    public void ThenTheIngestExecutorRunsFirst() =>
        Assert.Equal(CbixWorkflowNodes.Ingest, InvokedExecutorIds()[0]);

    [Then("the triage agent runs second, receiving the ingest output")]
    public void ThenTheTriageAgentRunsSecondReceivingTheIngestOutput()
    {
        Assert.Equal(CbixWorkflowNodes.Triage, InvokedExecutorIds()[1]);

        // "Receiving the ingest output" is a claim about the message on the edge, not about ordering,
        // so it is asserted on the payload triage produced: it carries the registered document, which
        // it can only have got from ingest.
        TriagedDocument triaged = CompletedPayload<TriagedDocument>(CbixWorkflowNodes.Triage);

        Assert.True(triaged.Document.IsNewRegistration);
        Assert.EndsWith(SpecimenFileName, triaged.Document.Submitted.FileName, StringComparison.Ordinal);
        Assert.NotNull(triaged.Document.TextLayer);
        Assert.Equal(CannedTriageAnswer, triaged.AgentResponse);

        // The model was actually consulted. Without this the step passes for a triage node that
        // fabricated a profile and never called anything - which is the failure the whole slot exists
        // to make impossible later.
        //
        // The count is the RUN's, not triage's, because by the time this step runs the whole graph has
        // executed: MAF's streaming run is drained before any Then step is reached. What it therefore
        // pins is that the run made exactly the calls the topology describes - no retry, no second
        // attempt, nothing calling a model twice.
        Assert.Equal(ModelCallsPerRun, ChatClient.RequestCount);
    }

    [Then("a downstream stub executor receives the triage output")]
    public void ThenADownstreamStubExecutorReceivesTheTriageOutput()
    {
        Assert.Equal(CbixWorkflowNodes.SectionExtraction, InvokedExecutorIds()[2]);

        SectionExtractionCandidate candidate =
            CompletedPayload<SectionExtractionCandidate>(CbixWorkflowNodes.SectionExtraction);

        Assert.Equal(CannedTriageAnswer, candidate.Document.AgentResponse);
    }

    [Then("the run completes at the persist step")]
    public void ThenTheRunCompletesAtThePersistStep()
    {
        Assert.Equal(CbixWorkflowNodes.Persist, InvokedExecutorIds()[^1]);

        // The workflow's own terminal signal, not merely the last executor to run. S01-09 asserts "the
        // run completes to the persist step", and an event stream that simply stopped would satisfy a
        // last-executor check while being indistinguishable from a stall.
        WorkflowOutputEvent output = Assert.Single(state.Events.OfType<WorkflowOutputEvent>());
        ExtractionRunOutcome outcome = Assert.IsType<ExtractionRunOutcome>(output.Data);

        Assert.Equal(CbixWorkflowNodes.Persist, output.ExecutorId);
        Assert.Equal(ExtractionRunDisposition.Persisted, outcome.Disposition);
        Assert.EndsWith(SpecimenFileName, outcome.Document.FileName, StringComparison.Ordinal);

        // A clean run is clean, not merely successful. The terminal node cannot suppress the
        // auto-send of its handler result on the pinned MAF version (see PersistStubExecutor's
        // remarks), so the graph emits one message that reaches no edge; this pins that the runtime
        // treats it as unremarkable. If a future version starts warning about it, the noise arrives in
        // every successful run's trace - at exactly the place an operator looks when a run did NOT
        // finish - and this is what will say so.
        Assert.Empty(state.Events.OfType<WorkflowWarningEvent>());
        Assert.Empty(state.Events.OfType<WorkflowErrorEvent>());
    }

    [Then("the triage agent is not called")]
    public void ThenTheTriageAgentIsNotCalled()
    {
        // Not called by THIS submission. The count is compared against the baseline the arranging step
        // recorded, so the assertion survives an arrangement that legitimately calls the model - and
        // says something stronger than "the counter is zero", which a broken arrangement would also
        // satisfy.
        Assert.Equal(state.ModelCallsBeforeSubmission, ChatClient.RequestCount);
        Assert.DoesNotContain(CbixWorkflowNodes.Triage, InvokedExecutorIds());
    }

    [Then("the run completes with the duplicate-ignored disposition")]
    public void ThenTheRunCompletesWithTheDuplicateIgnoredDisposition()
    {
        // Ingest and the duplicate terminal ran, and nothing else did: the run ended at the registry,
        // which is design 5.1's designed outcome for a re-submission rather than a failure.
        Assert.Equal(
            [CbixWorkflowNodes.Ingest, CbixWorkflowNodes.DuplicateTerminal],
            InvokedExecutorIds());

        // The point of the whole branch. This step used to assert the ABSENCE of an output event,
        // which pinned exactly the wrong semantic: "no output" is also what a crashed or stalled run
        // produces, so the assertion held for both and distinguished neither. Every run now yields
        // exactly one outcome and says on it how it ended.
        WorkflowOutputEvent output = Assert.Single(state.Events.OfType<WorkflowOutputEvent>());
        ExtractionRunOutcome outcome = Assert.IsType<ExtractionRunOutcome>(output.Data);

        Assert.Equal(CbixWorkflowNodes.DuplicateTerminal, output.ExecutorId);
        Assert.Equal(ExtractionRunDisposition.DuplicateIgnored, outcome.Disposition);
        Assert.Equal(0, outcome.SectionCount);
        Assert.EndsWith(SpecimenFileName, outcome.Document.FileName, StringComparison.Ordinal);

        Assert.Empty(state.Events.OfType<WorkflowWarningEvent>());
        Assert.Empty(state.Events.OfType<WorkflowErrorEvent>());
    }

    [Then("no provider adapter assembly took part in composing it")]
    public void ThenNoProviderAdapterAssemblyTookPartInComposingIt()
    {
        // The assemblies that actually contributed: the graph's own assembly, plus the declaring
        // assembly of every executor bound into it. Reflecting over the built graph rather than over a
        // project's references is what makes this a statement about composition and not about the
        // build.
        Workflow workflow = RunScope.ServiceProvider.GetRequiredService<CbixWorkflowFactory>().Build();

        List<Assembly> contributing =
        [
            typeof(CbixWorkflowFactory).Assembly,
            .. workflow.ReflectExecutors().Values
                .Select(binding => binding.ExecutorType)
                .Where(type => type is not null)
                .Select(type => type!.Assembly),
        ];

        List<string> offenders =
        [
            .. contributing
                .Select(assembly => assembly.GetName().Name ?? string.Empty)
                .Where(name => name.StartsWith("Cbix.Providers.", StringComparison.Ordinal)
                    || name.Contains("Anthropic", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.True(
            offenders.Count == 0,
            $"A provider assembly took part in composing the graph: {string.Join(", ", offenders)}. The "
                + "topology is the artefact S01-09's agnosticism proof runs, so a provider reaching into it "
                + "would make that proof circular.");

        // Guard the guard: the check is only meaningful if it inspected something.
        Assert.Contains(contributing, assembly => assembly == typeof(CbixWorkflowFactory).Assembly);
        Assert.True(contributing.Count > 1, "No executors were bound into the graph; the scan saw nothing.");
    }

    /// <summary>Composes Core's graph over the repository's data directory, with a canned agent in the triage slot.</summary>
    /// <remarks>
    /// <para>
    /// The ingest root is the repository's <c>data</c> directory and the specimen is read from it
    /// unmodified, so the containment gate, the hashing and the PDFPig text layer all run against a real
    /// PDF rather than a fixture built to be easy.
    /// </para>
    /// <para>
    /// <b>The composition itself is <c>Cbix.Agnosticism</c>'s, shared with story S01-09's proof rather
    /// than copied for it.</b> Both features need the identical thing - the production
    /// <c>AddCbixWorkflow</c> with a stub chat client behind the triage agent - and two copies would
    /// drift, leaving the agnosticism gate guarding a topology this feature no longer describes. It
    /// also puts the stub in an assembly with no provider on its reference table, which is what makes
    /// S01-09's dependency-graph assertion answerable at all.
    /// </para>
    /// <para>
    /// The text-only capability that composition configures is the one the whole run can be driven
    /// under with no provider present. That is not a way of avoiding the Claude profile: which profile
    /// a capability selects is proved by the composition tests, and this feature is about the topology.
    /// </para>
    /// </remarks>
    private void Compose()
    {
        StubChatClient chatClient = new(CannedTriageAnswer, CannedDeSpecimenReplies.DocControlSection());

        ServiceCollection services = [];
        services.AddLogging();
        services.AddStubBackedCbixWorkflow(RepositoryLayout.DataDirectory(), chatClient);

        ServiceProvider container = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        state.SpecimenPath = RepositoryLayout.DeSpecimenPath();
        state.ChatClient = chatClient;
        state.Container = container;

        StartNewRunScope();
    }

    /// <summary>Ends the current run's scope, if any, and opens the next one.</summary>
    /// <remarks>
    /// One scope per run is the shape <c>AddCbixWorkflow</c> documents and the composition tests
    /// assert, so a scenario that drives two runs has to drive two scopes. It is not bookkeeping: the
    /// scope is what bounds the document-content profile's "prepare each document once" memo, so
    /// reusing one across runs would let the second run see the first's prepared document - which is
    /// precisely the cross-run leak the scoped registration exists to prevent, quietly reintroduced by
    /// the test harness.
    /// </remarks>
    private void StartNewRunScope()
    {
        state.RunScope?.Dispose();
        state.RunScope = state.Container!.CreateScope();
    }

    /// <summary>Runs the graph once over the specimen, collecting every event the run raised.</summary>
    private async Task RunAsync()
    {
        Workflow workflow = RunScope.ServiceProvider.GetRequiredService<CbixWorkflowFactory>().Build();

        StreamingRun run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new DocumentSubmission(state.SpecimenPath!));

        await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync())
        {
            state.Events.Add(workflowEvent);
        }
    }

    /// <summary>The ids of the executors the run invoked, in the order the runtime invoked them.</summary>
    private List<string> InvokedExecutorIds() =>
        [.. state.Events.OfType<ExecutorInvokedEvent>().Select(invoked => invoked.ExecutorId)];

    /// <summary>The payload one executor completed with, typed.</summary>
    private T CompletedPayload<T>(string executorId)
        where T : class
    {
        ExecutorCompletedEvent completed = Assert.Single(
            state.Events.OfType<ExecutorCompletedEvent>(),
            candidate => string.Equals(candidate.ExecutorId, executorId, StringComparison.Ordinal));

        return Assert.IsType<T>(completed.Data);
    }

    /// <summary>The run scope, or a failure naming the step that should have created it.</summary>
    private IServiceScope RunScope =>
        state.RunScope ?? throw new InvalidOperationException(
            "No workflow was composed; a Given step must run before this one.");

    /// <summary>The canned chat client, or a failure naming the step that should have created it.</summary>
    private StubChatClient ChatClient =>
        state.ChatClient ?? throw new InvalidOperationException(
            "No chat client was composed; a Given step must run before this one.");
}
