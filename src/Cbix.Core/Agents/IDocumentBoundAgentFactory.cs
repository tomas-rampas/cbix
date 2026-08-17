using Cbix.Core.Documents;

namespace Cbix.Core.Agents;

/// <summary>
/// Creates the agent for one graph node, already bound to the document this run prepared.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why binding and construction are one call.</b> Handing a caller an agent and, separately,
/// something that attaches a document to it was tried and measured to be a trap: on the Anthropic
/// path the values on the object the raw-representation escape hatch returns <em>win</em> over the
/// agent's own chat options, so a binder that named a model would silently override the tier the
/// agent was built for - a Sonnet agent running on Haiku, visible only as degraded matrix accuracy.
/// Resolving the model, the output cap and the document in one place removes the possibility rather
/// than documenting it. That is why this port creates the agent instead of decorating one.
/// </para>
/// <para>
/// <b>Registered per graph node, keyed by node id</b> (<see cref="Cbix.Core.Workflows.CbixWorkflowNodes"/>),
/// exactly as the plain <c>AIAgent</c> registration was: the key <em>is</em> the role, and a node
/// asks for its own agent without learning which provider answers. Choosing the provider, the model
/// snapshot and the role's instructions stays a host decision, which is what keeps a provider swap a
/// configuration change.
/// </para>
/// <para>
/// <b>Two implementations exist, and the split is about transport, not about capability.</b>
/// <see cref="ChatContentDocumentAgentFactory"/> attaches the prepared blocks as ordinary chat
/// content, which is correct for every profile whose blocks are ordinary <c>AIContent</c> - the
/// text-only and generic-vision profiles, and any provider read through them. A provider whose
/// document block cannot travel as chat content supplies its own implementation from its adapter;
/// Anthropic's does, because MAF's integration silently drops an <c>AIContent</c> whose only payload
/// is <c>RawRepresentation</c>.
/// </para>
/// </remarks>
public interface IDocumentBoundAgentFactory
{
    /// <summary>Creates this node's agent, bound to one prepared document.</summary>
    /// <param name="document">
    /// The document as the active profile prepared it. Shared with the rest of the run and treated as
    /// read-only, per <see cref="IDocumentContentProvider.PrepareAsync"/>'s sharing contract.
    /// </param>
    /// <returns>
    /// An agent whose <see cref="BoundDocumentAgent.RunAsync"/> is the only supported way to reach the
    /// model with this document attached.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    BoundDocumentAgent CreateForDocument(DocumentContent document);
}
