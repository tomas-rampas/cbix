using Cbix.Core.Documents;
using Cbix.Core.Extraction;
using Cbix.Core.Ingest;
using Cbix.Core.Workflows;

namespace Cbix.UnitTests.Workflows;

/// <summary>
/// The message triage puts on the graph, and the predicate its routing edges consult
/// (stories S01-14, S01-15).
/// </summary>
/// <remarks>
/// <see cref="TriagedDocument.RequiresReview"/> is tested here rather than through the built graph
/// because MAF 1.17.0 exposes no public reader for an edge's condition - <c>DirectEdgeInfo</c>
/// surfaces <c>Connection</c>, <c>Kind</c>, <c>HasAssigner</c> and <c>HasCondition</c> and nothing
/// else. The three-layer split is deliberate rather than a workaround: this pins the decision, the
/// graph-shape tests pin that two conditional edges consult it, and the BDD lanes pin that a real run
/// takes the branch the decision names.
/// </remarks>
public sealed class TriagedDocumentTests
{
    [Theory]
    [InlineData(0.95d, false)]
    [InlineData(0.71d, false)]
    [InlineData(0.70d, false)]
    [InlineData(0.69d, true)]
    [InlineData(0.15d, true)]
    [InlineData(0.00d, true)]
    public void RequiresReview_ComparesConfidenceAgainstTheThreshold(double confidence, bool expected)
    {
        // The boundary case (0.70 against a 0.70 threshold) is the one worth spelling out: the
        // comparison is strict, so the configured number reads as "the lowest confidence that still
        // extracts". Left ambiguous, an operator setting 0.7 cannot tell which side of it a document
        // reported at 0.7 falls, and either answer is defensible - which is the problem.
        Assert.Equal(expected, Profiled(confidence).RequiresReview(0.70d));
    }

    [Fact]
    public void RequiresReview_TakesTheThresholdItIsGivenRatherThanAConstant()
    {
        // Design 11 says confidence thresholds are calibrated against observed correction rates, and
        // nobody has those rates yet - so the number must be an argument all the way down. One document
        // routes both ways depending only on what it is compared against.
        TriagedDocument document = Profiled(0.8d);

        Assert.False(document.RequiresReview(0.7d));
        Assert.True(document.RequiresReview(0.9d));
    }

    [Fact]
    public void RequiresReview_FailsClosedOnAConfidenceThatCannotBeCompared()
    {
        // NaN makes every comparison false, so `Confidence < threshold` would answer "does not require
        // review" and send a document nobody could score straight into extraction. The parser refuses
        // a non-finite confidence today, which makes this a SECOND guard - and that is the point: a
        // routing gate whose safety depends on a check in another class stops being safe the day that
        // class changes. Constructed directly, bypassing the parser, because that is exactly the path
        // the guard has to survive.
        TriagedDocument unscoreable = new(
            Ingested(),
            CbixWorkflowNodes.Triage,
            "{}",
            new DocumentProfile("UNKNOWN", "UNKNOWN", "UNKNOWN", "UNKNOWN", "UNKNOWN", double.NaN),
            profileParseFailure: null);

        Assert.True(unscoreable.RequiresReview(0.7d));
        Assert.True(unscoreable.RequiresReview(0d));
    }

    [Fact]
    public void RequiresReview_SendsARefusedReplyToReviewAtAnyThreshold()
    {
        // "Never guess", realised. A reply the contract refused carries strictly less information than
        // a low-confidence one, so it cannot proceed no matter how permissive the threshold - including
        // a threshold of zero, which admits every profile there is.
        TriagedDocument refused = Refused();

        Assert.True(refused.RequiresReview(0.7d));
        Assert.True(refused.RequiresReview(0d));
    }

    [Fact]
    public void Constructor_RefusesAMessageThatIsNeitherProfiledNorRefused()
    {
        // A document that is neither would reach the routing edges with nothing to route on. Both edges
        // would consult a predicate over an absent profile, and whichever way that resolved would be an
        // accident rather than a decision.
        Assert.Throws<ArgumentException>(() => new TriagedDocument(
            Ingested(),
            CbixWorkflowNodes.Triage,
            "{}",
            profile: null,
            profileParseFailure: null));
    }

    [Fact]
    public void Constructor_RefusesAMessageThatIsBothProfiledAndRefused()
    {
        // A consumer reading the profile would act on data the pipeline had already declined - and it
        // would be the consumer that happened to look at the profile first, which is not a decision
        // anyone made.
        Assert.Throws<ArgumentException>(() => new TriagedDocument(
            Ingested(),
            CbixWorkflowNodes.Triage,
            "{}",
            Profile(0.9d),
            "The triage agent's reply is not JSON."));
    }

    [Fact]
    public void Constructor_KeepsTheVerbatimReplyEvenWhenItParsed()
    {
        // Design 8's audit bar: which model and prompt produced a value, and what it actually said. A
        // parsed record cannot answer the last of those, and a run that discarded the reply on the happy
        // path would leave the question answerable only for the documents that failed.
        TriagedDocument document = Profiled(0.9d);

        Assert.Equal("{}", document.AgentResponse);
        Assert.Null(document.ProfileParseFailure);
        Assert.NotNull(document.Profile);
    }

    [Fact]
    public void Constructor_RefusesADuplicateSubmission()
    {
        // The backstop behind the edge out of ingest. A duplicate's run stops at the registry, so
        // nothing was prepared for it - triaging one means an agent was asked about a document that was
        // never prepared, which no downstream assertion would catch.
        DocumentReference submitted = Reference();

        DocumentIngestResult duplicate = new(
            submitted,
            Registered(submitted),
            isNewRegistration: false,
            textLayer: null,
            contentHandle: null);

        Assert.Throws<ArgumentException>(() => new TriagedDocument(
            duplicate,
            CbixWorkflowNodes.Triage,
            "{}",
            Profile(0.9d),
            profileParseFailure: null));
    }

    private static TriagedDocument Profiled(double confidence) =>
        new(Ingested(), CbixWorkflowNodes.Triage, "{}", Profile(confidence), profileParseFailure: null);

    private static TriagedDocument Refused() =>
        new(
            Ingested(),
            CbixWorkflowNodes.Triage,
            "not json",
            profile: null,
            profileParseFailure: "The triage agent's reply is not JSON.");

    private static DocumentProfile Profile(double confidence) =>
        new("Cross-Border Trading Legal Instruction", "DE", "CBTI-DE-2024", "4.2", "contoso-country-manual-v4", confidence);

    private static DocumentIngestResult Ingested()
    {
        DocumentReference submitted = Reference();

        return new DocumentIngestResult(
            submitted,
            Registered(submitted),
            isNewRegistration: true,
            new TextLayer(submitted.DocumentId, ["page one"]),
            contentHandle: null);
    }

    private static DocumentRegistryEntry Registered(DocumentReference submitted) =>
        new(
            new ContentHash("sha256", new string('a', 64)),
            submitted,
            byteLength: 1024,
            firstSeenUtc: DateTimeOffset.UnixEpoch);

    private static DocumentReference Reference() =>
        new(
            "sha256:" + new string('a', 64),
            new Uri(Path.Combine(Path.GetTempPath(), "cbix-triaged-document", "specimen.pdf")),
            "specimen.pdf");
}
