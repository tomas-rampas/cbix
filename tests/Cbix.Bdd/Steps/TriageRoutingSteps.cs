using System.Collections;
using System.Reflection;
using System.Text.Json;

using Cbix.Agnosticism;
using Cbix.Bdd.Support;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/TriageLowConfidenceRouting.feature</c> (story S01-15).
/// </summary>
/// <remarks>
/// <para>
/// <b>How "a document that is not a recognisable cross-border trading legal instruction" is
/// represented, stated plainly rather than glossed.</b> The document on disk is the committed DE
/// specimen; what makes the scenario an unrecognised-document scenario is the profile triage returns
/// for it - <c>UNKNOWN</c> fields and a confidence below the threshold. That is not a shortcut, it
/// is the only faithful representation available: with the model stubbed, "triage did not recognise
/// this" can only be expressed as what triage said, and the subject under test is the edge that acts
/// on it, not the model's judgement. The live judgement is S01-14's <c>@requiresApi</c> lane. Adding
/// a second binary fixture to the repository to say the same thing would cost a committed PDF and
/// prove nothing extra.
/// </para>
/// <para>
/// The review queue is reached by reflection for the reason the S01-12 and S01-14 bindings record:
/// the port does not exist when the scenarios are first written, and a red phase must be a failing
/// assertion naming the missing behaviour rather than a build break that names nothing.
/// </para>
/// </remarks>
[Binding]
public sealed class TriageRoutingSteps(TriageRoutingState state)
{
    [AfterScenario]
    public void DisposeScenarioResources() => state.Dispose();

    [Given("a document that is not a recognisable cross-border trading legal instruction")]
    public void GivenAnUnrecognisedDocument() =>
        state.CannedReply = UnrecognisedProfileJson();

    [Given("a document whose triage reply does not match the DocumentProfile contract")]
    public void GivenAReplyTheContractRefuses() =>
        state.CannedReply = "I am not sure what this document is. It looks like a manual of some kind.";

    [Given("a document triage recognises with a confidence above the routing threshold")]
    public void GivenARecognisedDocument() =>
        state.CannedReply = RecognisedProfileJson(confidence: 0.85d);

    [Given("a deployment whose triage review threshold is configured to {double}")]
    public void GivenAConfiguredThreshold(double threshold) =>
        state.Routing = new TriageRoutingOptions(threshold);

    [When("triage runs and returns a DocumentProfile with confidence below the routing threshold")]
    public Task WhenTriageReturnsALowConfidenceProfile() => RunAsync();

    [When("the workflow runs")]
    public Task WhenTheWorkflowRuns() => RunAsync();

    [Then("the workflow routes to the review-queue stub")]
    public void ThenTheWorkflowRoutesToTheReviewQueueStub()
    {
        IList rows = QueuedRows();

        Assert.True(
            rows.Count == 1,
            $"The review queue holds {rows.Count} rows for a document triage could not vouch for. Design 9 "
                + "answers an unrecognised document by routing the whole document to review; a run that "
                + "wrote no row has routed it nowhere, and a human will never see it.");

        Assert.Equal(ReviewNodeId(), Assert.Single(state.Events.OfType<WorkflowOutputEvent>()).ExecutorId);
    }

    [Then("no section agent runs for this document")]
    public void ThenNoSectionAgentRuns()
    {
        // The money half of the gate. A run that queued the document for review AND ran the extractors
        // anyway would look correct from the queue's side and be paid for twice - once for the agents,
        // once for the reviewer.
        Assert.DoesNotContain(CbixWorkflowNodes.SectionExtraction, InvokedExecutorIds());
        Assert.DoesNotContain(CbixWorkflowNodes.Persist, InvokedExecutorIds());
    }

    [Then("the run emits exactly one outcome, queued for review")]
    public void ThenTheRunEmitsExactlyOneReviewOutcome()
    {
        // S01-13's invariant, which this story must not spend: EVERY terminal branch yields exactly one
        // WorkflowOutputEvent carrying its disposition. A review branch that simply stopped would be
        // indistinguishable from a stalled run to anything watching the stream - the exact ambiguity
        // the duplicate terminal was built to remove.
        WorkflowOutputEvent output = Assert.Single(state.Events.OfType<WorkflowOutputEvent>());
        ExtractionRunOutcome outcome = Assert.IsType<ExtractionRunOutcome>(output.Data);

        Assert.Equal("ReviewQueued", outcome.Disposition.ToString());
        Assert.Equal(0, outcome.SectionCount);
        Assert.EndsWith(RepositoryLayout.DeSpecimenFileName, outcome.Document.FileName, StringComparison.Ordinal);

        Assert.Empty(state.Events.OfType<WorkflowWarningEvent>());
        Assert.Empty(state.Events.OfType<WorkflowErrorEvent>());
    }

    [Then("the queued row says the reply was refused")]
    public void ThenTheQueuedRowSaysTheReplyWasRefused()
    {
        object row = QueuedRows()[0]!;

        // A queue row nobody can triage is a to-do list with no priorities. The two ways a document
        // arrives here - the model was unsure, and the model's reply was not the contract - want
        // different responses from a human, so the row has to distinguish them without anyone reading
        // free text.
        Assert.Equal("TriageReplyRefused", Read(row, "Reason")?.ToString());

        string? detail = Read(row, "Detail") as string;
        Assert.False(
            string.IsNullOrWhiteSpace(detail),
            "The review-queue row carries no detail, so a reviewer is told a document needs attention and "
                + "nothing about why.");
    }

    [Then("no review-queue row is written")]
    public void ThenNoReviewQueueRowIsWritten() =>
        Assert.True(
            QueuedRows().Count == 0,
            "A document triage vouched for was queued for review anyway. An over-eager gate is a pipeline "
                + "that has automated nothing, and it is the failure mode nobody reports because everything "
                + "still gets done.");

    [Then("the run reaches the persist step rather than the review queue")]
    public void ThenTheRunReachesThePersistStep()
    {
        // The control for the whole feature. Without it every assertion above is satisfied by a graph
        // that routes EVERYTHING to review, which would be a perfectly green suite and a useless
        // pipeline.
        WorkflowOutputEvent output = Assert.Single(state.Events.OfType<WorkflowOutputEvent>());
        ExtractionRunOutcome outcome = Assert.IsType<ExtractionRunOutcome>(output.Data);

        Assert.Equal(CbixWorkflowNodes.Persist, output.ExecutorId);
        Assert.Equal(ExtractionRunDisposition.Persisted, outcome.Disposition);
        Assert.Contains(CbixWorkflowNodes.SectionExtraction, InvokedExecutorIds());
    }

    /// <summary>Composes the production graph with a canned triage reply, then runs it once.</summary>
    private async Task RunAsync()
    {
        ServiceCollection services = [];
        services.AddLogging();

        if (state.Routing is { } routing)
        {
            // Registered BEFORE Core's composition, which is how a host supplies it: Core's own
            // registration is TryAdd-shaped, so a configured value wins over the default without either
            // side knowing about the other.
            services.AddSingleton(routing);
        }

        // Two replies, in call order. Only the FIRST is this feature's subject - what triage said is
        // what the routing edge acts on - but the control scenario deliberately lets a document through
        // to extraction, and a section agent reached with no reply of its own would fail that run for a
        // reason the feature is not about.
        services.AddStubBackedCbixWorkflow(
            RepositoryLayout.DataDirectory(),
            new StubChatClient(
                state.CannedReply ?? throw new InvalidOperationException(
                    "No canned triage reply was chosen; a Given step must run before the When."),
                CannedDeSpecimenReplies.DocControlSection()));

        ServiceProvider container = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        state.Container = container;
        state.RunScope = container.CreateScope();
        state.SpecimenPath = RepositoryLayout.DeSpecimenPath();

        Workflow workflow = state.RunScope.ServiceProvider.GetRequiredService<CbixWorkflowFactory>().Build();

        StreamingRun run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new DocumentSubmission(state.SpecimenPath));

        await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync())
        {
            state.Events.Add(workflowEvent);
        }
    }

    /// <summary>The rows the run put in the review queue, read through the neutral port.</summary>
    private IList QueuedRows()
    {
        Type port = CoreType(
            "Cbix.Core.Review.IReviewQueue",
            "so nothing in the graph can record that a document needs a human, and design 6's review_queue "
                + "table has no in-process counterpart");

        IServiceScope scope = state.RunScope
            ?? throw new InvalidOperationException("No workflow was composed; a When step must run first.");

        object queue = scope.ServiceProvider.GetService(port)
            ?? throw new InvalidOperationException(
                $"'{port.FullName}' is not registered, so the review-queue node has nothing to write to.");

        return Read(queue, "Entries") as IList
            ?? throw new InvalidOperationException(
                $"'{queue.GetType().FullName}' exposes no readable 'Entries', so a scenario cannot see what "
                    + "was queued.");
    }

    /// <summary>The graph's review node id, located on the node registry so a rename is an assertion.</summary>
    private static string ReviewNodeId() =>
        typeof(CbixWorkflowNodes)
            .GetField("Review", BindingFlags.Public | BindingFlags.Static)
            ?.GetRawConstantValue() as string
        ?? throw new InvalidOperationException(
            $"'{nameof(CbixWorkflowNodes)}' declares no 'Review', so the graph has no review node and the "
                + "conditional edge out of triage has nowhere to go.");

    private static Type CoreType(string fullName, string consequence) =>
        typeof(CbixWorkflowFactory).Assembly.GetType(fullName, throwOnError: false)
            ?? throw new InvalidOperationException($"'{fullName}' does not exist, {consequence}.");

    private static object? Read(object instance, string propertyName) =>
        instance.GetType()
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(instance);

    private List<string> InvokedExecutorIds() =>
        [.. state.Events.OfType<ExecutorInvokedEvent>().Select(invoked => invoked.ExecutorId)];

    /// <summary>A profile in which triage says, honestly, that it does not know.</summary>
    /// <remarks>
    /// The <c>UNKNOWN</c> sentinel with a low confidence is the channel design 5.3 gives a model for
    /// "I could not identify this" - the alternative the prompt explicitly forbids is inventing values.
    /// A parser that refused this shape would leave a model no way to say it.
    /// </remarks>
    private static string UnrecognisedProfileJson() =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DocType"] = "UNKNOWN",
            ["JurisdictionIso"] = "UNKNOWN",
            ["DocRef"] = "UNKNOWN",
            ["Version"] = "UNKNOWN",
            ["LayoutFamily"] = "UNKNOWN",
            ["Confidence"] = 0.15d,
        });

    private static string RecognisedProfileJson(double confidence) =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["DocType"] = "Cross-Border Trading Legal Instruction",
            ["JurisdictionIso"] = "DE",
            ["DocRef"] = "CBTI-DE-2024",
            ["Version"] = "4.2",
            ["LayoutFamily"] = "contoso-country-manual-v4",
            ["Confidence"] = confidence,
        });
}
