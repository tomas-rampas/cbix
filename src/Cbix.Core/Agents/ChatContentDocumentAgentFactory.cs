using Cbix.Core.Documents;

using Microsoft.Agents.AI;

namespace Cbix.Core.Agents;

/// <summary>
/// Binds a prepared document to an agent the ordinary way: as chat content on the turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the default, and it is the correct one for every profile Core itself can build.</b>
/// The text-only and generic-vision profiles present a document as ordinary
/// <see cref="Microsoft.Extensions.AI.AIContent"/> - page markers, verbatim page text, and (for
/// vision) rendered page images - and ordinary content is exactly what a chat turn carries. Nothing
/// provider-specific is involved, which is why this lives in Core and why the whole pipeline can be
/// driven by a stub <c>IChatClient</c> with no adapter anywhere on the path (design 3, story S01-09).
/// </para>
/// <para>
/// <b>It does not ask which profile prepared the content, and it must not</b> -
/// <see cref="IDocumentContentProvider"/> states plainly that no caller may branch on which profile
/// is in play. Which binding a deployment gets is a composition decision: a provider whose blocks
/// cannot travel as chat content registers its own <see cref="IDocumentBoundAgentFactory"/> for the
/// node, and that registration wins because Core's is <c>TryAdd</c>-shaped.
/// </para>
/// <para>
/// <b>It does, however, refuse content this path cannot carry, and an earlier version of this remark
/// was wrong about who does that.</b> It claimed a host that configured a native-document capability
/// and forgot its adapter's factory would get "a loud refusal from the adapter's own attachment
/// code". That code is not on this path at all: the misconfiguration lands <em>here</em>, and it was
/// measured passing every check - MAF dropped the raw-only block, the model answered fluently about a
/// document it was never shown, and triage reported a profile above the review threshold, so no
/// human was ever asked. The refusal now exists where the mistake arrives, in
/// <see cref="BoundDocumentAgent"/>'s chat-attached constructor, and it is a question about a block's
/// own payload ("is there anything here a chat turn can express") rather than about which profile
/// produced it - which is why it does not violate the rule above.
/// </para>
/// </remarks>
public sealed class ChatContentDocumentAgentFactory : IDocumentBoundAgentFactory
{
    private readonly AIAgent _agent;

    /// <summary>Initialises a new <see cref="ChatContentDocumentAgentFactory"/>.</summary>
    /// <param name="agent">
    /// The node's agent, as framework currency. Reused across documents: an agent holds a client, not
    /// per-document state, and the document is supplied per call.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <see langword="null"/>.</exception>
    public ChatContentDocumentAgentFactory(AIAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        _agent = agent;
    }

    /// <inheritdoc />
    public BoundDocumentAgent CreateForDocument(DocumentContent document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new BoundDocumentAgent(_agent, document.Content);
    }
}
