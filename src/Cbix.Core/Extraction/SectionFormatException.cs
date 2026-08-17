namespace Cbix.Core.Extraction;

/// <summary>
/// A section agent's reply did not match its section contract, and no section was produced from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A typed refusal, not a parse failure to be recovered from.</b> The governing principle is that
/// agents propose and code disposes: a reply that does not match the agreed contract is a proposal
/// the code declines. Silently repairing one - filling a missing envelope, coercing a type, accepting
/// a value with no snippet - would let the model decide what the pipeline records, which is exactly
/// the authority it must not have.
/// </para>
/// <para>
/// <b>It fails the run rather than routing anywhere, and that is a scope decision with an owner.</b>
/// Triage's low-confidence edge routes to review because triage's whole job is routing. A section
/// agent returning something that is not its contract is a validator problem: design 5.6's
/// <c>ValidationReport</c> and its retry edge re-invoke <em>only</em> the failing agent with the
/// findings in context, for at most two attempts, before escalating to review. Building routing for
/// it here would be that story done in the wrong place and without its scenarios, so until Sprint 02
/// this surfaces as a failed executor - loud, typed and unrouted.
/// </para>
/// <para>
/// <b>Nothing the model wrote is echoed into <see cref="Exception.Message"/> unsanitised.</b> The
/// reply is untrusted text from outside the estate; every model-supplied fragment a refusal needs is
/// rendered through the boundary's shared sanitiser first, and no value is ever quoted whole.
/// </para>
/// </remarks>
public sealed class SectionFormatException : Exception
{
    /// <summary>Initialises a new <see cref="SectionFormatException"/>.</summary>
    /// <param name="section">The section whose contract was broken, for example <c>DocControl</c>.</param>
    /// <param name="defect">Which part of the contract the reply broke.</param>
    /// <param name="message">A description safe to log and to store.</param>
    /// <param name="innerException">The underlying JSON failure, when there was one.</param>
    /// <exception cref="ArgumentException"><paramref name="section"/> is empty or white space.</exception>
    public SectionFormatException(
        string section,
        SectionDefect defect,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);

        Section = section;
        Defect = defect;
    }

    /// <summary>Initialises a new <see cref="SectionFormatException"/> with a default description.</summary>
    /// <remarks>
    /// Present because <c>CA1032</c> asks an exception type for the standard constructors. It names
    /// the least specific defect available, so a rethrow can never accidentally assert a more precise
    /// diagnosis than it has.
    /// </remarks>
    public SectionFormatException()
        : this("section", SectionDefect.NotAJsonObject, "The section reply does not match its contract.")
    {
    }

    /// <summary>Initialises a new <see cref="SectionFormatException"/> with a description.</summary>
    /// <param name="message">A description safe to log and to store.</param>
    public SectionFormatException(string message)
        : this("section", SectionDefect.NotAJsonObject, message)
    {
    }

    /// <summary>Initialises a new <see cref="SectionFormatException"/> with a description and cause.</summary>
    /// <param name="message">A description safe to log and to store.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SectionFormatException(string message, Exception? innerException)
        : this("section", SectionDefect.NotAJsonObject, message, innerException)
    {
    }

    /// <summary>
    /// Gets the section whose contract was broken.
    /// </summary>
    /// <remarks>
    /// Carried on the exception because Sprint 02's retry edge re-invokes <em>only</em> the failing
    /// agent, and "which agent" is the first thing it needs. With seven agents in the fan-out, an
    /// untagged refusal would leave the retry either re-running everything or guessing.
    /// </remarks>
    public string Section { get; }

    /// <summary>Gets which part of the contract the reply broke.</summary>
    public SectionDefect Defect { get; }
}
