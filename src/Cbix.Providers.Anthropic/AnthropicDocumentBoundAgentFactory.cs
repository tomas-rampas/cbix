using Cbix.Core.Agents;
using Cbix.Core.Documents;

namespace Cbix.Providers.Anthropic;

/// <summary>
/// Supplies one graph node's agent, bound to the run's document by whichever route that document's
/// profile actually needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the adapter's half of the neutral seam.</b> <c>Cbix.Core</c> declares
/// <see cref="IDocumentBoundAgentFactory"/> and ships the chat-content implementation; this one
/// exists because content prepared by the Claude native-PDF profile cannot travel that way -
/// measured in S01-05, MAF's Anthropic integration silently drops an <c>AIContent</c> whose only
/// payload is <c>RawRepresentation</c>, so the document has to go through the integration's escape
/// hatch instead. A node holding this factory gets that for free and never learns it happened.
/// </para>
/// <para>
/// <b>Why it branches on its own profile, when callers are forbidden to branch on any.</b>
/// <see cref="IDocumentContentProvider"/>'s rule is that code <em>outside</em> a profile must not ask
/// which profile is in play; the rule exists so that presentation stays a composition decision. This
/// type is not outside: it is the adapter that owns the native-PDF profile, and the question it asks
/// is "did I prepare this". The combination that makes the question necessary is a real deployment -
/// an Anthropic-backed worker configured for <c>Vision</c> or <c>TextOnly</c> presentation because an
/// egress proxy blocks the Files API (design 8) - and in that configuration the content is ordinary
/// multimodal blocks that belong in the message. Refusing it instead, which is what routing
/// everything through the escape hatch would do, would turn a supported configuration into a hard
/// failure on the first document.
/// </para>
/// <para>
/// <b>Model and output cap are resolved inside the adapter, per call, together.</b> The values on the
/// object the escape hatch returns were measured to <em>win</em> over the agent's own chat options,
/// so an arrangement in which one component named the model and another built the agent is a silent
/// tiering bug - a Sonnet-tier agent running on Haiku, visible only as degraded accuracy.
/// <see cref="AnthropicAgentFactory.CreateDocumentAgent"/> resolves both once and hands them to a
/// binding that rebuilds the request itself; this type simply forwards the role's own overrides to
/// it.
/// </para>
/// <para>
/// <b>An agent per document, and that is cheap on purpose.</b> An agent is a stateless caller over
/// the factory's pooled client, so constructing one per run costs an object rather than an HTTP
/// stack. What must not be shared is the <em>binding</em>: it carries one document, and reusing one
/// across runs is how a run gets shown another run's document.
/// </para>
/// </remarks>
internal sealed class AnthropicDocumentBoundAgentFactory : IDocumentBoundAgentFactory
{
    private readonly AnthropicAgentFactory _factory;
    private readonly string _name;
    private readonly string _instructions;
    private readonly string? _modelId;
    private readonly int? _maxOutputTokens;

    /// <summary>Initialises a new <see cref="AnthropicDocumentBoundAgentFactory"/>.</summary>
    /// <param name="factory">The adapter factory that owns the client.</param>
    /// <param name="name">Agent name as used in the workflow graph.</param>
    /// <param name="instructions">The agent's system instructions.</param>
    /// <param name="modelId">Exact dated snapshot, or <see langword="null"/> for the configured default.</param>
    /// <param name="maxOutputTokens">Per-response output cap, or <see langword="null"/> for the configured default.</param>
    internal AnthropicDocumentBoundAgentFactory(
        AnthropicAgentFactory factory,
        string name,
        string instructions,
        string? modelId,
        int? maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);

        _factory = factory;
        _name = name;
        _instructions = instructions;
        _modelId = modelId;
        _maxOutputTokens = maxOutputTokens;
    }

    /// <inheritdoc />
    public BoundDocumentAgent CreateForDocument(DocumentContent document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.Equals(
            document.Capabilities.ProfileName,
            ClaudeDocumentContentProvider.ProfileName,
            StringComparison.Ordinal))
        {
            return _factory.CreateDocumentAgent(_name, _instructions, document, _modelId, _maxOutputTokens);
        }

        // Ordinary multimodal content, so the ordinary route. Core's binding puts the blocks at the
        // head of the user turn, which is where the provider-attached path puts the document block
        // too - so the prefix a prompt cache keys on is the same shape under either presentation.
        return new BoundDocumentAgent(
            _factory.CreateAgent(_name, _instructions, _modelId, _maxOutputTokens),
            document.Content);
    }
}
