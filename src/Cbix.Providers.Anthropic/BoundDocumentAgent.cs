using global::Anthropic.Models.Beta.Messages;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Cbix.Providers.Anthropic;

/// <summary>
/// An agent that always shows the model one prepared document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The document is attached by <see cref="RunAsync"/> and by nothing else, which is what makes
/// the guarantee real.</b> An earlier shape handed the caller an agent and a matching options object
/// and asked them to pass both; that was forgeable, and measurably so - pairing a Sonnet agent with
/// another binding's options put Haiku on the wire, and pairing a plain agent with document options
/// injected a document into an agent that was never meant to see one. Both failures are silent. The
/// options are therefore internal, they are rebuilt from scratch on every call, and running the
/// agent any other way simply does not carry the document.
/// </para>
/// <para>
/// <b>Rebuilding per call also closes a mutability hole.</b> <see cref="ChatOptions"/> is a mutable
/// class: a shared instance handed out once could have its
/// <see cref="ChatOptions.RawRepresentationFactory"/> nulled by any holder, after which every later
/// call would quietly send no document. Nothing outside this type ever holds the options, and each
/// call gets its own.
/// </para>
/// <para>
/// <b>Interim confinement, and where this is going.</b> <see cref="RunAsync"/> returns MAF's
/// <see cref="AgentResponse"/>, so a caller of this type takes a dependency on
/// <c>Microsoft.Agents.AI</c> - neutral framework currency, not a provider type, but a dependency
/// the solution's containment rules still care about. Until S01-13 relocates the neutral callable
/// into <c>Cbix.Core</c> along with that dependency, <b>binding a document to an agent is confined
/// to the Worker composition root</b>: the workflow and the executors receive whatever neutral
/// abstraction S01-13 lands, not this class. That relocation is a recorded plan decision, not an
/// aspiration.
/// </para>
/// <para>
/// <b>Reuse across calls is expected.</b> The blocks are immutable inputs, so one binding serves
/// every question a section agent asks about its document - which is what the prompt cache wants,
/// since each call then presents an identical document prefix.
/// </para>
/// </remarks>
public sealed class BoundDocumentAgent
{
    private readonly IReadOnlyList<BetaContentBlockParam> _blocks;
    private readonly string _model;
    private readonly int _maxOutputTokens;

    /// <summary>Initialises a new <see cref="BoundDocumentAgent"/>.</summary>
    /// <param name="agent">The agent, as the framework abstraction.</param>
    /// <param name="blocks">The document blocks to present on every call.</param>
    /// <param name="model">The exact dated snapshot the agent was built for.</param>
    /// <param name="maxOutputTokens">The per-response output cap the agent was built with.</param>
    /// <remarks>
    /// Internal, and constructed only by <see cref="AnthropicAgentFactory.CreateDocumentAgent"/>:
    /// <paramref name="model"/> and <paramref name="maxOutputTokens"/> must be the values the agent
    /// itself was built with, and the factory is where both are resolved once so they cannot drift.
    /// </remarks>
    internal BoundDocumentAgent(
        AIAgent agent,
        IReadOnlyList<BetaContentBlockParam> blocks,
        string model,
        int maxOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(blocks);

        Agent = agent;
        _blocks = blocks;
        _model = model;
        _maxOutputTokens = maxOutputTokens;
    }

    /// <summary>
    /// Gets the underlying agent, for the properties a caller legitimately needs - its name, its
    /// identity in telemetry and the extraction-run record.
    /// </summary>
    /// <remarks>
    /// <b>Running this directly sends no document.</b> It is exposed because an agent's name is part
    /// of the provenance record (design 8) and hiding the whole object to protect one method would
    /// cost more than it buys. Use <see cref="RunAsync"/> for anything that must see the document.
    /// </remarks>
    public AIAgent Agent { get; }

    /// <summary>
    /// Runs one turn with the prepared document attached.
    /// </summary>
    /// <param name="message">The prompt for this turn - the section agent's question.</param>
    /// <param name="cancellationToken">Token that cancels the call.</param>
    /// <returns>The agent's response.</returns>
    /// <remarks>
    /// The request is assembled here, per call, from the blocks and the model this binding was
    /// created with. That is the only supported way to reach the model with the document attached,
    /// and the reason is on the type: every alternative shape that has been tried could be
    /// mis-assembled without any error being raised.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="message"/> is <see langword="null"/>, empty, or white space.</exception>
    public Task<AgentResponse> RunAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return Agent.RunAsync(message, options: CreateRunOptions(), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds a fresh set of run options carrying the document.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the wire-level tests can assert what this produces - including
    /// the measured precedence of the raw request over the agent's own chat options, which is a
    /// property of the integration and therefore has to be pinned by a test rather than assumed.
    /// </remarks>
    internal ChatClientAgentRunOptions CreateRunOptions() =>
        new(new ChatOptions
        {
            RawRepresentationFactory = _ => ClaudeDocumentAttachment.BuildRequest(_blocks, _model, _maxOutputTokens),
        });
}
