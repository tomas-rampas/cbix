namespace Cbix.Core.Workflows;

/// <summary>
/// How a workflow run ended.
/// </summary>
/// <remarks>
/// <para>
/// Every terminal branch of the graph reports one of these, which is what makes "the run finished"
/// answerable without knowing which path it took. The alternative - inferring the outcome from which
/// node happened to be last, or from the absence of an output - was measured to be a trap: a run
/// that ends early and a run that stalls produce the same silence.
/// </para>
/// <para>
/// A run that <em>fails</em> has no value here on purpose. A failure surfaces as MAF's own
/// <c>ExecutorFailedEvent</c> and <c>WorkflowErrorEvent</c> with the exception attached, and giving
/// it a disposition too would invite code to treat a failure as just another outcome to switch on.
/// </para>
/// </remarks>
public enum ExtractionRunDisposition
{
    /// <summary>
    /// The document was extracted and reached the persist step.
    /// </summary>
    /// <remarks>
    /// Named for what the branch will do rather than what the Sprint 01 stub does. Sprint 03 makes
    /// the write real; the disposition does not change when it does.
    /// </remarks>
    Persisted = 0,

    /// <summary>
    /// The document was already registered, so the run stopped at the registry without extracting
    /// anything (design 5.1, design 9 "Duplicate submission").
    /// </summary>
    /// <remarks>
    /// This is a success, not a failure: the pipeline did exactly what a re-submission asks of it, and
    /// the audit trail carries a <c>DuplicateSubmissionIgnored</c> entry to say so. It is a distinct
    /// value rather than folded into <see cref="Persisted"/> because an operator counting published
    /// documents and an operator counting completed runs are asking different questions.
    /// </remarks>
    DuplicateIgnored = 1,
}
