using System.Collections.ObjectModel;

using Microsoft.Extensions.AI;

namespace Cbix.Core.Documents;

/// <summary>
/// A document prepared for presentation to a model: the content blocks an agent attaches to its
/// chat call, the capability descriptor saying how faithfully those blocks represent the document,
/// and the serializable handle that lets the same content be re-obtained after a resume.
/// </summary>
/// <remarks>
/// <para>
/// <b>This value is in-process only. It is never a workflow message and never a checkpoint
/// payload.</b> <see cref="Content"/> holds provider payloads in
/// <see cref="AIContent.RawRepresentation"/>, which is <c>[JsonIgnore]</c>: a System.Text.Json
/// round trip - what MAF superstep checkpointing performs - returns an object that deserialises
/// cleanly and no longer contains the document. Persist <see cref="Handle"/> instead, and re-obtain
/// this value from <see cref="IDocumentContentProvider.PrepareAsync"/> after a resume.
/// </para>
/// <para>
/// <see cref="Content"/> is expressed in <see cref="AIContent"/>, the provider-neutral currency
/// shared by <c>IChatClient</c> and MAF agents. A profile that needs a provider-specific block -
/// a Claude Files API document block, for instance - carries it in
/// <see cref="AIContent.RawRepresentation"/> on its own content item. That keeps the provider
/// type inside the profile assembly, which is the whole point of the port.
/// </para>
/// <para>
/// This is a class rather than a record because it holds a content collection: identity
/// semantics are honest here, whereas record equality would imply a structural comparison that
/// a list of <see cref="AIContent"/> does not actually provide.
/// </para>
/// </remarks>
public sealed class DocumentContent
{
    /// <summary>Initialises a new <see cref="DocumentContent"/>.</summary>
    /// <param name="content">
    /// The content blocks to attach to an agent's chat call, in the order they should appear.
    /// Copied defensively, so a caller that keeps and mutates the list it passed cannot change a
    /// document that has already been prepared and possibly cached. Must contain at least one
    /// block and no null elements: a provider that can present nothing has failed, and should
    /// throw <see cref="DocumentPreparationException"/> rather than return a result that reads as
    /// success.
    /// </param>
    /// <param name="capabilities">How faithfully <paramref name="content"/> represents the document.</param>
    /// <param name="handle">
    /// The serializable handle for this preparation. Its
    /// <see cref="DocumentContentHandle.ProfileName"/> must match
    /// <see cref="DocumentContentCapabilities.ProfileName"/>: a resume redeems the handle against
    /// the profile named in it, so a mismatch would checkpoint a handle nobody can honour.
    /// </param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="content"/> is empty or contains a null element, or the handle's profile name
    /// does not match the capability descriptor's.
    /// </exception>
    public DocumentContent(
        IReadOnlyList<AIContent> content,
        DocumentContentCapabilities capabilities,
        DocumentContentHandle handle)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(handle);

        if (content.Count == 0)
        {
            throw new ArgumentException("Prepared document content must contain at least one block.", nameof(content));
        }

        AIContent[] copy = [.. content];
        for (int index = 0; index < copy.Length; index++)
        {
            if (copy[index] is null)
            {
                throw new ArgumentException(
                    $"Prepared document content must not contain null blocks; block {index} was null.",
                    nameof(content));
            }
        }

        if (!string.Equals(handle.ProfileName, capabilities.ProfileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The handle names profile '{handle.ProfileName}' but the capabilities describe profile "
                    + $"'{capabilities.ProfileName}'; a resume would redeem the handle against the wrong profile.",
                nameof(handle));
        }

        // Wrapped, not just copied. An AIContent[] handed out as IReadOnlyList<AIContent> can be
        // cast straight back to an array and written through, which matters here because one
        // prepared document is shared by the seven-way parallel section fan-out.
        Content = new ReadOnlyCollection<AIContent>(copy);
        Capabilities = capabilities;
        Handle = handle;
    }

    /// <summary>
    /// Gets the content blocks to attach to an agent's chat call, in presentation order.
    /// <para>
    /// The collection is a read-only view over a private copy, but the blocks in it are mutable and
    /// are shared across the concurrent section-agent fan-out: read them, never write to them. See
    /// <see cref="IDocumentContentProvider.PrepareAsync"/> for the sharing contract.
    /// </para>
    /// </summary>
    public IReadOnlyList<AIContent> Content { get; }

    /// <summary>Gets the description of how faithfully <see cref="Content"/> represents the document.</summary>
    public DocumentContentCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the serializable handle for this preparation - the part of it that run state and
    /// checkpoints persist, and that a resumed run passes back to
    /// <see cref="IDocumentContentProvider.PrepareAsync"/>.
    /// </summary>
    public DocumentContentHandle Handle { get; }
}
