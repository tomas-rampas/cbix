namespace Cbix.Core.Documents;

/// <summary>
/// Thrown when an <see cref="IDocumentContentProvider"/> cannot present a document to the model.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline's two failure responses are different, so the failure has to say which one it
/// wants: a <see cref="IsTransient"/> failure is retried with backoff (design 9), while a
/// permanent one goes to the review queue. Leaving that to a caller's exception-type guesswork is
/// how a bad document ends up retried three times and how a rate limit ends up in a human's queue.
/// </para>
/// <para>
/// The constructors deliberately do not include the conventional parameterless and message-only
/// overloads: transience is never a default, it is a decision the throwing profile is in the best
/// position to make.
/// </para>
/// </remarks>
public sealed class DocumentPreparationException : Exception
{
    /// <summary>Initialises a new <see cref="DocumentPreparationException"/>.</summary>
    /// <param name="message">Description of the failure, safe for operational logs.</param>
    /// <param name="isTransient">
    /// <see langword="true"/> when a later attempt could plausibly succeed unchanged - a timeout,
    /// a rate limit, a provider 5xx - so the caller should retry with backoff;
    /// <see langword="false"/> when the failure is a property of the document or configuration -
    /// an unreadable file, a rejected media type, a missing credential - so the caller must route
    /// to review instead of burning retries.
    /// </param>
    public DocumentPreparationException(string message, bool isTransient)
        : base(message)
    {
        IsTransient = isTransient;
    }

    /// <summary>Initialises a new <see cref="DocumentPreparationException"/> wrapping the underlying failure.</summary>
    /// <param name="message">Description of the failure, safe for operational logs.</param>
    /// <param name="isTransient">See the two-argument constructor.</param>
    /// <param name="innerException">The provider or I/O exception that caused this failure.</param>
    public DocumentPreparationException(string message, bool isTransient, Exception? innerException)
        : base(message, innerException)
    {
        IsTransient = isTransient;
    }

    /// <summary>
    /// Gets a value indicating whether a later attempt could plausibly succeed unchanged, and the
    /// caller should therefore retry with backoff rather than route the run to human review.
    /// </summary>
    public bool IsTransient { get; }
}
