namespace Cbix.Core.Ingest;

/// <summary>
/// Thrown when the ingest gate refuses a submission because the bytes it names are reachable from
/// outside the configured ingest root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is its own type.</b> Refusing a path is a security decision, not a malformed-input
/// nuisance, and it is the one ingest outcome a caller may want to treat specially: quarantine the
/// submission, alert, and never retry it, because retrying will refuse it again. Folding it into a
/// general <see cref="ArgumentException"/> would make that decision depend on parsing a message,
/// and would let a broad <c>catch</c> upstream silently turn a rejected credentials file into an
/// ordinary bad argument.
/// </para>
/// <para>
/// <b>What it protects.</b> <c>DocumentReference</c> validates shape - an absolute, local
/// <c>file://</c> URI - and explicitly delegates containment to the ingest layer. Shape alone is not
/// enough: a path to a private key or a cloud-credentials file is perfectly well-formed, and once
/// admitted it is a document like any other, which a document-content profile would upload to a
/// third-party model provider.
/// </para>
/// <para>
/// <b>The message carries no paths.</b> <see cref="Exception.Message"/> is derived from
/// <see cref="Reason"/> alone and is a constant string. The paths that caused the refusal are
/// unvalidated attacker-influenced input: interpolating them into a message that flows into logs is
/// the log-injection and path-disclosure vector <c>DocumentReference</c> rejects control characters
/// to avoid, and re-introducing it on the refusal path - the one path that only ever handles input
/// somebody else chose - would be precisely backwards. The paths remain available, typed, on
/// <see cref="SubmittedPath"/>, <see cref="ResolvedPath"/> and <see cref="IngestRoot"/>, where a
/// caller can log them as structured fields rather than as message text.
/// </para>
/// <para>
/// The conventional parameterless and message-only constructors are deliberately absent: an instance
/// with no reason and no paths cannot say what was refused, which is the whole content of the event.
/// </para>
/// </remarks>
public sealed class IngestRootViolationException : Exception
{
    /// <summary>Initialises a new <see cref="IngestRootViolationException"/>.</summary>
    /// <param name="reason">Which containment rule refused the submission.</param>
    /// <param name="submittedPath">The path exactly as submitted, before any resolution.</param>
    /// <param name="resolvedPath">
    /// The path after normalisation and link resolution - the location actually judged, and the one
    /// worth reading when the submitted path looked innocuous. Equal to
    /// <paramref name="submittedPath"/> when the refusal happened before any resolution.
    /// </param>
    /// <param name="ingestRoot">The configured root the resolved path had to lie under.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="reason"/> is not a defined value, or any path argument is empty or white
    /// space.
    /// </exception>
    public IngestRootViolationException(
        IngestRootViolationReason reason,
        string submittedPath,
        string resolvedPath,
        string ingestRoot)
        : base(DescribeSafely(reason, submittedPath, resolvedPath, ingestRoot))
    {
        Reason = reason;
        SubmittedPath = submittedPath;
        ResolvedPath = resolvedPath;
        IngestRoot = ingestRoot;
    }

    /// <summary>Gets which containment rule refused the submission.</summary>
    public IngestRootViolationReason Reason { get; }

    /// <summary>Gets the path exactly as submitted, before normalisation or link resolution.</summary>
    public string SubmittedPath { get; }

    /// <summary>Gets the fully resolved path that was judged against the ingest root.</summary>
    public string ResolvedPath { get; }

    /// <summary>Gets the configured ingest root the resolved path had to lie under.</summary>
    public string IngestRoot { get; }

    /// <summary>
    /// Validates the arguments and returns a constant message for the reason.
    /// </summary>
    /// <remarks>
    /// Called from the constructor's <c>base(...)</c> argument so that validation happens
    /// <em>before</em> the message is produced. Validating in the constructor body instead would run
    /// it after <see cref="Exception"/>'s constructor had already accepted whatever was passed,
    /// which for a type whose whole job is to describe refused input is the wrong order.
    /// </remarks>
    private static string DescribeSafely(
        IngestRootViolationReason reason,
        string submittedPath,
        string resolvedPath,
        string ingestRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submittedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ingestRoot);

        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentException($"'{reason}' is not a defined ingest containment refusal reason.", nameof(reason));
        }

        return reason switch
        {
            IngestRootViolationReason.OutsideIngestRoot =>
                "The submitted document resolves to a location outside the configured ingest root.",
            IngestRootViolationReason.UncOrDevicePath =>
                "The submitted document path is a UNC or device-namespace path, which ingest refuses without resolving it.",
            IngestRootViolationReason.MultiplyLinkedFile =>
                "The submitted document has more than one hard link, so the same bytes are reachable by a name outside the ingest root.",
            _ => "The submitted document was refused by the ingest containment boundary.",
        };
    }
}
