using System.Reflection;

using Cbix.Core.Ingest;
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
        Assert.Equal([CbixWorkflowNodes.SectionExtraction], SinksOf(workflow, CbixWorkflowNodes.Triage));
        Assert.Equal([CbixWorkflowNodes.Persist], SinksOf(workflow, CbixWorkflowNodes.SectionExtraction));

        // Both terminals are terminal. A node with an outgoing edge is not an end state, and the whole
        // point of the duplicate branch is that it ENDS somewhere observable.
        Assert.Empty(SinksOf(workflow, CbixWorkflowNodes.Persist));
        Assert.Empty(SinksOf(workflow, CbixWorkflowNodes.DuplicateTerminal));
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
    public void Build_MakesTriageTheOnlyModelCallingNodeToday()
    {
        // Pins the premise the stagger test rests on. That test checks the model-calling nodes it is
        // told about, so a new agent node added without extending ModelCallingNodes would be checked
        // by nothing at all. This fails when the set of executors holding an AIAgent changes, which is
        // the moment someone must decide whether the new one belongs downstream of triage.
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
    public void Build_LeavesTriageWithAnUnconditionalEdgeForStoryS0115ToSplit()
    {
        // Pins the starting point rather than the destination. S01-15 turns this single unconditional
        // edge into two conditional ones - onward, and to the review queue below the routing threshold
        // - and this test failing is the correct, expected signal that it did so. Its value is now:
        // nothing today silently routes on a confidence nobody has computed yet.
        Workflow workflow = BuildWorkflow();

        DirectEdgeInfo edge = Assert.IsType<DirectEdgeInfo>(
            Assert.Single(workflow.ReflectEdges()[CbixWorkflowNodes.Triage]));

        Assert.False(edge.HasCondition);
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
    public void Constructor_RejectsAMissingNode(int missingNode)
    {
        DocumentIngestExecutor ingest = CreateIngestExecutor();
        TriageExecutor triage = CreateTriageExecutor();
        SectionExtractionStubExecutor section = new();
        PersistStubExecutor persist = new(NullLogger<PersistStubExecutor>.Instance);
        DuplicateTerminalExecutor duplicate = new();

        Assert.Throws<ArgumentNullException>(() => new CbixWorkflowFactory(
            missingNode == 0 ? null! : ingest,
            missingNode == 1 ? null! : triage,
            missingNode == 2 ? null! : section,
            missingNode == 3 ? null! : persist,
            missingNode == 4 ? null! : duplicate));
    }

    /// <summary>
    /// The nodes that call a model, and therefore the ones the cache-priming stagger constrains.
    /// </summary>
    /// <remarks>
    /// Maintained by hand and cross-checked against the graph by
    /// <see cref="Build_MakesTriageTheOnlyModelCallingNodeToday"/>, so it cannot silently fall behind
    /// the executors that actually hold an agent. Sprint 02 adds the seven section agents here, and
    /// the stagger test then has something to say about each of them.
    /// </remarks>
    private static readonly string[] ModelCallingNodes = [CbixWorkflowNodes.Triage];

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

    /// <summary>Reports whether an executor type holds an <see cref="AIAgent"/> - i.e. can call a model.</summary>
    /// <remarks>
    /// By field type rather than by name: an agent-holding executor must store the agent somewhere, and
    /// a naming convention would be satisfied by calling the field something else. This is the same
    /// question the stagger cares about - "does this node cost a model call" - asked of the type
    /// itself.
    /// </remarks>
    private static bool HoldsAnAgent(Type executorType) =>
        Array.Exists(
            executorType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => typeof(AIAgent).IsAssignableFrom(field.FieldType));

    private static Workflow BuildWorkflow() => CreateFactory().Build();

    private static CbixWorkflowFactory CreateFactory() =>
        new(
            CreateIngestExecutor(),
            CreateTriageExecutor(),
            new SectionExtractionStubExecutor(),
            new PersistStubExecutor(NullLogger<PersistStubExecutor>.Instance),
            new DuplicateTerminalExecutor());

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

    private static TriageExecutor CreateTriageExecutor() =>
        new(
            new ChatClientAgent(new UnusedChatClient(), name: CbixWorkflowNodes.Triage),
            NullLogger<TriageExecutor>.Instance);

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
