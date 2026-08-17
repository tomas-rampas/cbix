using System.Reflection;

using Cbix.Core.Agents;
using Cbix.Core.Documents;
using Cbix.Core.Ingest;
using Cbix.Core.Review;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cbix.UnitTests.Workflows;

/// <summary>
/// Structural tests for the graph <see cref="CbixWorkflowFactory"/> builds (story S01-13).
/// </summary>
/// <remarks>
/// <para>
/// These assert on the graph's <em>shape</em> - which nodes exist, which edges connect them, which
/// edge carries a condition - rather than on what a run does with it. The BDD scenarios cover the run.
/// Both are needed and they fail differently: a run test tells you the pipeline broke, a shape test
/// tells you which edge changed, and the shape is what the next three stories extend.
/// </para>
/// <para>
/// The shape is read back through MAF's own reflection API rather than through the builder calls, so
/// what is asserted is the graph the runtime will schedule and not a transcript of the fluent chain
/// that produced it.
/// </para>
/// </remarks>
public sealed class CbixWorkflowFactoryTests
{
    [Fact]
    public void Build_StartsAtIngest()
    {
        Workflow workflow = BuildWorkflow();

        // Not a formality. The start node is where the submission enters, and it is the one node whose
        // position cannot be inferred from the edges - a graph whose start was moved to triage would
        // have identical edges and would call a model on an unprepared document.
        Assert.Equal(CbixWorkflowNodes.Ingest, workflow.StartExecutorId);
    }

    [Fact]
    public void Build_BindsExactlyTheNodesOfTheSpineAndBothTerminals()
    {
        Workflow workflow = BuildWorkflow();

        Assert.Equal(
            [
                CbixWorkflowNodes.DuplicateTerminal,
                CbixWorkflowNodes.Ingest,
                CbixWorkflowNodes.Persist,
                CbixWorkflowNodes.Review,
                CbixWorkflowNodes.SectionExtraction,
                CbixWorkflowNodes.Triage,
            ],
            workflow.ReflectExecutors().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Build_ConnectsTheSpineInOrderAndBranchesDuplicatesOutOfIngest()
    {
        Workflow workflow = BuildWorkflow();

        Assert.Equal(
            [CbixWorkflowNodes.DuplicateTerminal, CbixWorkflowNodes.Triage],
            SinksOf(workflow, CbixWorkflowNodes.Ingest));
        Assert.Equal(
            [CbixWorkflowNodes.Review, CbixWorkflowNodes.SectionExtraction],
            SinksOf(workflow, CbixWorkflowNodes.Triage));
        Assert.Equal([CbixWorkflowNodes.Persist], SinksOf(workflow, CbixWorkflowNodes.SectionExtraction));

        // Every terminal is terminal. A node with an outgoing edge is not an end state, and the whole
        // point of the duplicate and review branches is that they END somewhere observable.
        Assert.Empty(SinksOf(workflow, CbixWorkflowNodes.Persist));
        Assert.Empty(SinksOf(workflow, CbixWorkflowNodes.DuplicateTerminal));
        Assert.Empty(SinksOf(workflow, CbixWorkflowNodes.Review));
    }

    [Fact]
    public void Build_GivesIngestExactlyTwoConditionalEdges()
    {
        // Design 5.1 says a re-submission's run stops at the registry. Expressing that as edge
        // conditions rather than as a check inside triage is what makes it a property of the topology:
        // a reader of the graph can see that ingest's output splits, and every future node hung off
        // ingest inherits the decision.
        //
        // TWO edges, both conditional, is the specific shape - and the count is the assertion that
        // matters. One conditional edge was the earlier shape and it left the duplicate branch ending
        // in silence, indistinguishable from a stalled run. An unconditional edge here would send
        // duplicates to the triage agent: a paid model call about a document nobody prepared.
        Workflow workflow = BuildWorkflow();

        List<DirectEdgeInfo> edges =
        [
            .. workflow.ReflectEdges()[CbixWorkflowNodes.Ingest].Select(Assert.IsType<DirectEdgeInfo>),
        ];

        Assert.Equal(2, edges.Count);
        Assert.All(
            edges,
            edge => Assert.True(
                edge.HasCondition,
                "An edge out of ingest carries no condition, so every submission - duplicates included - "
                    + "would take it."));
    }

    [Fact]
    public void Build_KeepsEveryModelCallingNodeDownstreamOfTriage()
    {
        // THE CACHE-PRIMING STAGGER, as a test rather than as a paragraph. An Anthropic prompt-cache
        // entry becomes readable only once the response that writes it has begun, so a fan-out that
        // runs alongside triage all cache-misses and every branch pays the write premium instead of
        // collecting the discount. The cost of losing this is measured money, and - this is the part
        // that makes prose insufficient - nothing else fails: the extraction is correct and the bill
        // is wrong.
        //
        // Stated as reachability rather than as "triage is first", because that is the property Sprint
        // 02 must preserve when it replaces the single sectionExtraction node with a seven-way
        // fan-out: the fan-out may be as wide as it likes, provided it descends FROM triage rather
        // than sitting beside it. An edge added from ingest to the fan-out would fail this.
        Workflow workflow = BuildWorkflow();

        HashSet<string> downstreamOfTriage = Descendants(workflow, CbixWorkflowNodes.Triage);

        List<string> unstaggered =
        [
            .. ModelCallingNodes
                .Where(node => !string.Equals(node, CbixWorkflowNodes.Triage, StringComparison.Ordinal))
                .Where(node => !downstreamOfTriage.Contains(node))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            unstaggered.Count == 0,
            $"These model-calling nodes are not downstream of triage: {string.Join(", ", unstaggered)}. "
                + "Triage is the cache primer; anything that calls a model beside it rather than after it "
                + "cache-misses and pays the write premium. Nothing else will report this.");

        // Guard the guard. If the reachability walk returned nothing - a renamed node, a changed edge
        // API - the assertion above would pass over an empty set while the property had stopped
        // holding.
        Assert.Contains(CbixWorkflowNodes.SectionExtraction, downstreamOfTriage);
    }

    [Fact]
    public void Build_PinsExactlyWhichNodesCallAModel()
    {
        // Pins the premise the stagger test rests on. That test checks the model-calling nodes it is
        // told about, so a new agent node added without extending ModelCallingNodes would be checked
        // by nothing at all. This fails when the set of executors that can reach a model changes,
        // which is the moment someone must decide whether the new one belongs downstream of triage.
        //
        // It fired exactly as intended when story S01-16 put the DocControl agent in the section slot:
        // the set went from one node to two, and extending it was a decision rather than a merge
        // artefact. Sprint 02 adds the remaining six and the stagger test then has something to say
        // about each of them.
        Workflow workflow = BuildWorkflow();

        List<string> agentNodes =
        [
            .. workflow.ReflectExecutors()
                .Where(binding => binding.Value.ExecutorType is { } type && HoldsAnAgent(type))
                .Select(binding => binding.Key)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(ModelCallingNodes.Order(StringComparer.Ordinal), agentNodes);
    }

    [Fact]
    public void Build_GivesTriageExactlyTwoConditionalEdges()
    {
        // The S01-15 split, and the replacement for the test that pinned the unconditional edge this
        // one supersedes. That test's own comment said its failure would be the expected signal that
        // the edge had been split; this is what the signal was for.
        //
        // TWO edges, both conditional, is the specific shape - the same arrangement ingest uses, for
        // the same reason. One conditional edge would leave a document that failed the predicate
        // matching nothing at all: the run would go quiet, no outcome would be emitted, and "the
        // pipeline declined to vouch for this" and "the run stalled" would be the same observation.
        // An unconditional edge would send every document to extraction on a confidence nobody
        // consulted, which is exactly what design 9 says must not happen.
        Workflow workflow = BuildWorkflow();

        List<DirectEdgeInfo> edges =
        [
            .. workflow.ReflectEdges()[CbixWorkflowNodes.Triage].Select(Assert.IsType<DirectEdgeInfo>),
        ];

        Assert.Equal(2, edges.Count);
        Assert.All(
            edges,
            edge => Assert.True(
                edge.HasCondition,
                "An edge out of triage carries no condition, so every document - including ones triage "
                    + "could not identify - would take it."));

        Assert.Equal(
            [CbixWorkflowNodes.Review, CbixWorkflowNodes.SectionExtraction],
            SinksOf(workflow, CbixWorkflowNodes.Triage));
    }

    [Fact]
    public void Build_MakesEveryTerminalBranchTerminal()
    {
        // S01-13's invariant, re-pinned now that there are three terminals rather than two: a run emits
        // exactly one outcome, whichever way it ended. This asserts the half a graph-shape test can -
        // that no terminal has an outgoing edge - and the half it cannot, that each is designated an
        // OUTPUT node, is asserted behaviourally instead: MAF 1.17.0 exposes no public reader for the
        // output designation (Workflow surfaces Name, Description, StartExecutorId, ExecutorBindings
        // and the Reflect* helpers, and nothing else), so the BDD lanes assert on the single
        // WorkflowOutputEvent a real run emits, which is the property the designation exists to give.
        Workflow workflow = BuildWorkflow();

        foreach (string terminal in (string[])
            [CbixWorkflowNodes.Persist, CbixWorkflowNodes.DuplicateTerminal, CbixWorkflowNodes.Review])
        {
            Assert.Empty(SinksOf(workflow, terminal));
        }
    }

    [Fact]
    public void Build_DoesNotCacheTheGraph()
    {
        // The narrow claim, and the only one two reference comparisons can support: Build() constructs
        // rather than memoises. That is what the per-run-scope arrangement needs - a cached instance
        // would be shared by whatever else held the same factory - and it is deliberately NOT a claim
        // that two graphs share no state, which reference inequality cannot establish and which is
        // false anyway: both graphs are built over the same executor instances, on purpose, because
        // those are the scope's.
        CbixWorkflowFactory factory = CreateFactory();

        Assert.NotSame(factory.Build(), factory.Build());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Constructor_RejectsAMissingNode(int missingNode)
    {
        DocumentIngestExecutor ingest = CreateIngestExecutor();
        TriageExecutor triage = CreateTriageExecutor();
        DocControlExecutor section = CreateDocControlExecutor();
        PersistStubExecutor persist = new(NullLogger<PersistStubExecutor>.Instance);
        DuplicateTerminalExecutor duplicate = new();
        ReviewQueueStubExecutor review = new(
            new InMemoryReviewQueue(),
            TimeProvider.System,
            NullLogger<ReviewQueueStubExecutor>.Instance);
        TriageRoutingOptions routing = new();

        Assert.Throws<ArgumentNullException>(() => new CbixWorkflowFactory(
            missingNode == 0 ? null! : ingest,
            missingNode == 1 ? null! : triage,
            missingNode == 2 ? null! : section,
            missingNode == 3 ? null! : persist,
            missingNode == 4 ? null! : duplicate,
            missingNode == 5 ? null! : review,
            missingNode == 6 ? null! : routing));
    }

    /// <summary>
    /// The nodes that call a model, and therefore the ones the cache-priming stagger constrains.
    /// </summary>
    /// <remarks>
    /// Maintained by hand and cross-checked against the graph by
    /// <see cref="Build_PinsExactlyWhichNodesCallAModel"/>, so it cannot silently fall behind the
    /// executors that actually reach a model. Story S01-16 added the section slot; Sprint 02 adds the
    /// remaining six section agents, and the stagger test then has something to say about each.
    /// </remarks>
    private static readonly string[] ModelCallingNodes =
        [CbixWorkflowNodes.Triage, CbixWorkflowNodes.SectionExtraction];

    /// <summary>The executor ids an edge out of <paramref name="sourceId"/> leads to.</summary>
    private static IReadOnlyList<string> SinksOf(Workflow workflow, string sourceId) =>
        workflow.ReflectEdges().TryGetValue(sourceId, out HashSet<EdgeInfo>? edges)
            ? [.. edges.SelectMany(edge => edge.Connection.SinkIds).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>Every node reachable from <paramref name="sourceId"/> by following edges.</summary>
    /// <remarks>
    /// A breadth-first walk with a visited set, so a future retry edge looping back into the graph
    /// (design 5.2 sends a failed validation back to the Matrix agent) cannot make this run forever.
    /// </remarks>
    private static HashSet<string> Descendants(Workflow workflow, string sourceId)
    {
        HashSet<string> reached = new(StringComparer.Ordinal);
        Queue<string> pending = new([sourceId]);

        while (pending.Count > 0)
        {
            foreach (string sink in SinksOf(workflow, pending.Dequeue()))
            {
                if (reached.Add(sink))
                {
                    pending.Enqueue(sink);
                }
            }
        }

        return reached;
    }

    /// <summary>Reports whether an executor type can reach a model - i.e. whether its node costs a call.</summary>
    /// <remarks>
    /// <para>
    /// By field type rather than by name: an agent-holding executor must store the agent somewhere, and
    /// a naming convention would be satisfied by calling the field something else. This is the same
    /// question the stagger cares about - "does this node cost a model call" - asked of the type
    /// itself.
    /// </para>
    /// <para>
    /// <b>Three types count, and the list grew with story S01-14 for a concrete reason rather than
    /// speculatively.</b> An executor may hold a bare <see cref="AIAgent"/>, a
    /// <see cref="BoundDocumentAgent"/>, or - as triage now does - an
    /// <see cref="IDocumentBoundAgentFactory"/> that hands one out per document. S01-14 replaced
    /// <c>TriageExecutor</c>'s <see cref="AIAgent"/> field with the factory, so the original
    /// one-type test stopped recognising the only model-calling node in the graph and had to be
    /// widened for the detector to keep answering the question it was written to answer.
    /// </para>
    /// <para>
    /// <b>An earlier version of this note claimed the consequence would have been a vacuous pass; that
    /// was backwards.</b> <see cref="Build_PinsExactlyWhichNodesCallAModel"/> compares against
    /// a NON-EMPTY expected set, so a detector that recognised nothing would have failed loudly, not
    /// silently - which is the arrangement working. What the widening bought is that it kept failing
    /// for the right reason: the detector answers "does this node cost a model call", and a node that
    /// reaches a model through a factory costs one exactly as much as one holding the agent directly.
    /// </para>
    /// </remarks>
    private static bool HoldsAnAgent(Type executorType) =>
        Array.Exists(
            executorType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => typeof(AIAgent).IsAssignableFrom(field.FieldType)
                || typeof(BoundDocumentAgent).IsAssignableFrom(field.FieldType)
                || typeof(IDocumentBoundAgentFactory).IsAssignableFrom(field.FieldType));

    private static Workflow BuildWorkflow() => CreateFactory().Build();

    private static CbixWorkflowFactory CreateFactory() =>
        new(
            CreateIngestExecutor(),
            CreateTriageExecutor(),
            CreateDocControlExecutor(),
            new PersistStubExecutor(NullLogger<PersistStubExecutor>.Instance),
            new DuplicateTerminalExecutor(),
            new ReviewQueueStubExecutor(
                new InMemoryReviewQueue(),
                TimeProvider.System,
                NullLogger<ReviewQueueStubExecutor>.Instance),
            new TriageRoutingOptions());

    /// <summary>
    /// An ingest executor over a temporary root.
    /// </summary>
    /// <remarks>
    /// The root is never read: these tests build graphs and never run them, and
    /// <see cref="DocumentIngestOptions"/> requires a fully qualified path rather than an existing one.
    /// </remarks>
    private static DocumentIngestExecutor CreateIngestExecutor()
    {
        DocumentIngestOptions options = new(
            Path.Combine(Path.GetTempPath(), "cbix-graph-shape"),
            DocumentIngestOptions.ClaudeFilesApiLimitBytes);

        return new DocumentIngestExecutor(
            new DocumentIngestService(
                new InMemoryDocumentRegistry(),
                new InMemoryIngestAuditLog(),
                options,
                new PdfPigTextLayerExtractor(options, NullLogger<PdfPigTextLayerExtractor>.Instance),
                NullLogger<DocumentIngestService>.Instance),
            NullLogger<DocumentIngestExecutor>.Instance);
    }

    /// <summary>A DocControl executor over an agent that refuses to run, like the triage one above.</summary>
    private static DocControlExecutor CreateDocControlExecutor()
    {
        DocumentIngestOptions options = new(
            Path.Combine(Path.GetTempPath(), "cbix-graph-shape"),
            DocumentIngestOptions.ClaudeFilesApiLimitBytes);

        return new DocControlExecutor(
            new ChatContentDocumentAgentFactory(
                new ChatClientAgent(new UnusedChatClient(), name: CbixWorkflowNodes.SectionExtraction)),
            new TextOnlyDocumentContentProfile(
                new PdfPigTextLayerExtractor(options, NullLogger<PdfPigTextLayerExtractor>.Instance)),
            NullLogger<DocControlExecutor>.Instance);
    }

    private static TriageExecutor CreateTriageExecutor()
    {
        DocumentIngestOptions options = new(
            Path.Combine(Path.GetTempPath(), "cbix-graph-shape"),
            DocumentIngestOptions.ClaudeFilesApiLimitBytes);

        return new TriageExecutor(
            new ChatContentDocumentAgentFactory(
                new ChatClientAgent(new UnusedChatClient(), name: CbixWorkflowNodes.Triage)),
            new TextOnlyDocumentContentProfile(
                new PdfPigTextLayerExtractor(options, NullLogger<PdfPigTextLayerExtractor>.Instance)),
            NullLogger<TriageExecutor>.Instance);
    }

    /// <summary>A chat client that refuses every call, because these tests build graphs and never run them.</summary>
    /// <remarks>
    /// Throwing rather than returning a canned answer is deliberate: if a shape test ever started
    /// executing the graph, it should say so loudly instead of quietly becoming a slow integration
    /// test that happens to pass.
    /// </remarks>
    private sealed class UnusedChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The graph-shape tests must not run the workflow.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The graph-shape tests must not run the workflow.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
