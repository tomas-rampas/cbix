namespace Cbix.Core.Extraction;

/// <summary>
/// A triage reply did not match the <see cref="DocumentProfile"/> contract, and no profile was
/// produced from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A typed refusal, not a parse failure to be recovered from.</b> The governing principle is that
/// agents propose and code disposes: a reply that does not match the agreed contract is a proposal
/// the code declines, and the only correct outcomes are "route this document to a human" (story
/// S01-15) or "fail the run loudly". Silently repairing one - filling a missing field, coercing a
/// string to a number, ignoring an extra key - would let the model decide what the pipeline records,
/// which is exactly the authority it must not have.
/// </para>
/// <para>
/// <b>Nothing the model wrote is echoed into <see cref="Exception.Message"/> unsanitised.</b> The
/// reply is untrusted text from outside the estate: control characters forge log lines, and format
/// characters (U+202E and its relatives) reorder whatever is rendered after them. The parser
/// therefore renders any model-supplied fragment - an undeclared field's name is the only one it
/// needs - through the same replace-and-bound treatment the ingest boundary applies to a submitted
/// path, and never quotes a value.
/// </para>
/// </remarks>
public sealed class DocumentProfileFormatException : Exception
{
    /// <summary>Initialises a new <see cref="DocumentProfileFormatException"/>.</summary>
    /// <param name="defect">Which part of the contract the reply broke.</param>
    /// <param name="message">A description safe to log and to store on a review-queue row.</param>
    /// <param name="innerException">The underlying JSON failure, when there was one.</param>
    public DocumentProfileFormatException(
        DocumentProfileDefect defect,
        string message,
        Exception? innerException = null)
        : base(message, innerException) => Defect = defect;

    /// <summary>Initialises a new <see cref="DocumentProfileFormatException"/> with a default description.</summary>
    /// <remarks>
    /// Present because <c>CA1032</c> asks an exception type for the standard constructors, and
    /// because a caller that catches and rethrows should not be forced to invent a defect. It
    /// defaults to <see cref="DocumentProfileDefect.NotAJsonObject"/>, the least specific claim
    /// available, so a rethrow can never accidentally assert a more precise diagnosis than it has.
    /// </remarks>
    public DocumentProfileFormatException()
        : this(DocumentProfileDefect.NotAJsonObject, "The triage reply is not a DocumentProfile.")
    {
    }

    /// <summary>Initialises a new <see cref="DocumentProfileFormatException"/> with a description.</summary>
    /// <param name="message">A description safe to log and to store on a review-queue row.</param>
    public DocumentProfileFormatException(string message)
        : this(DocumentProfileDefect.NotAJsonObject, message)
    {
    }

    /// <summary>Initialises a new <see cref="DocumentProfileFormatException"/> with a description and cause.</summary>
    /// <param name="message">A description safe to log and to store on a review-queue row.</param>
    /// <param name="innerException">The underlying failure.</param>
    public DocumentProfileFormatException(string message, Exception? innerException)
        : this(DocumentProfileDefect.NotAJsonObject, message, innerException)
    {
    }

    /// <summary>Gets which part of the contract the reply broke.</summary>
    public DocumentProfileDefect Defect { get; }
}
