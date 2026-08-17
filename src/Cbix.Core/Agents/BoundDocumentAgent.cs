using System.Collections.ObjectModel;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Cbix.Core.Agents;

/// <summary>
/// An agent that always shows the model one prepared document.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the guarantee is, stated exactly.</b> <b>Call-site forgery is impossible</b>:
/// <see cref="RunAsync"/> takes no options, the options are built by a factory this type owns and
/// rebuilt from scratch per call, and nothing exposes them - so no caller can pair an agent with the
/// wrong document, or with document options it was never meant to see. That is the failure this
/// shape was built to remove, and it was a measured one: an earlier design handed the caller an agent
/// and a matching options object and asked them to pass both, which put Haiku on the wire for a
/// Sonnet-tier agent and injected documents into agents that should not have had them, both silently.
/// </para>
/// <para>
/// <b>Binding forgery is possible, and is accepted.</b> The constructor is public, so anyone can
/// construct a <see cref="BoundDocumentAgent"/> pairing any agent with any options factory. That is
/// not an oversight being papered over: <c>Cbix.Core</c> cannot restrict construction to the provider
/// adapters, because it does not know what they are and must not - an <c>InternalsVisibleTo</c> list
/// of provider assembly names in Core would invert the very containment this arrangement exists to
/// enforce. The residual risk is small and different in kind: a mis-paired binding requires someone to
/// deliberately construct one, whereas the shape this replaced made mis-pairing the default way to
/// get it wrong. In practice bindings come from <c>CreateDocumentAgent</c> on a provider adapter,
/// which resolves the model and the cap once and hands both to the same closure.
/// </para>
/// <para>
/// <b>Rebuilding per call also closes a mutability hole.</b> The options a provider builds are
/// mutable objects: a shared instance handed out once could have its document-carrying member
/// nulled by any holder, after which every later call would quietly send no document. Nothing
/// outside this type ever holds the options, and each call gets its own.
/// </para>
/// <para>
/// <b>Why this lives in <c>Cbix.Core</c> (story S01-13).</b> It was born in
/// <c>Cbix.Providers.Anthropic</c>, as a deliberate, recorded interim confinement: its members name
/// only framework currency, but reaching it meant referencing the adapter, so binding a document to
/// an agent was confined to the Worker composition root. Nothing about the type was ever
/// provider-specific - <see cref="AIAgent"/>, <see cref="AgentRunOptions"/> and
/// <see cref="AgentResponse"/> are MAF abstractions - and Core now carries the MAF dependency for
/// <c>WorkflowBuilder</c> anyway. So it moves here and the adapter returns it. Concretely: an
/// executor can hold a document-bound agent without any project reference to a provider, which is
/// what makes "a provider swap is configuration" true of the workflow rather than only of the
/// composition root.
/// </para>
/// <para>
/// <b>What the provider still owns.</b> Everything inside the options: which request shape carries a
/// document, which betas it needs, which model snapshot it names. The provider-specific half stays
/// behind the adapter boundary; only the call shape is shared. That half arrives as the run-options
/// factory this type is constructed with, and this type never inspects it.
/// </para>
/// <para>
/// <b>Reuse across calls is expected.</b> The prepared document is an immutable input, so one
/// binding serves every question a section agent asks about its document - which is what the prompt
/// cache wants, since each call then presents an identical document prefix.
/// </para>
/// <para>
/// <b>Two ways a document reaches the model, and the split is about transport rather than
/// capability (story S01-14).</b> A profile whose blocks are ordinary <see cref="AIContent"/> - the
/// text-only and generic-vision profiles - travels the ordinary way: the blocks lead the user turn,
/// the prompt follows them, and no run options are involved. A provider whose document block cannot
/// survive as chat content travels through run options instead; Anthropic's must, because MAF's
/// integration was measured silently dropping an <see cref="AIContent"/> whose only payload is
/// <c>RawRepresentation</c>. Which one a binding uses is fixed when it is constructed, by whoever
/// knew which profile prepared the content - never by a caller and never by inspecting the blocks.
/// </para>
/// </remarks>
public sealed class BoundDocumentAgent
{
    private readonly Func<AgentRunOptions>? _runOptionsFactory;
    private readonly IReadOnlyList<AIContent>? _documentBlocks;

    /// <summary>Initialises a new <see cref="BoundDocumentAgent"/>.</summary>
    /// <param name="agent">The agent, as the framework abstraction.</param>
    /// <param name="runOptionsFactory">
    /// Builds the run options that carry the prepared document. Invoked once per call to
    /// <see cref="RunAsync"/> and never shared, so a returned options object is reachable by nobody
    /// but the call it was built for. It must return a fresh instance each time; returning a cached
    /// one reopens the mutability hole described on this type.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Public rather than internal, unlike the adapter-owned shape this replaces, and the consequence
    /// is stated plainly on the type: anyone can construct a mis-paired binding. Core cannot be a
    /// friend of every present and future adapter assembly, and an <c>InternalsVisibleTo</c> list of
    /// provider names in Core would be the containment inversion this whole arrangement exists to
    /// prevent. What the public constructor does not weaken is the call-site guarantee: however a
    /// binding was built, the options it uses never escape it, so no <em>caller</em> can spoil a call
    /// that a correctly built binding makes.
    /// </remarks>
    public BoundDocumentAgent(AIAgent agent, Func<AgentRunOptions> runOptionsFactory)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(runOptionsFactory);

        Agent = agent;
        _runOptionsFactory = runOptionsFactory;
    }

    /// <summary>
    /// Initialises a binding that carries the document as ordinary chat content.
    /// </summary>
    /// <param name="agent">The agent, as the framework abstraction.</param>
    /// <param name="documentBlocks">
    /// The prepared document's blocks, in presentation order. They lead every user turn this binding
    /// sends, so the prompt always arrives after the document - the ordering a prefix cache needs,
    /// and the same ordering the provider-attached path enforces in the request it builds.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="documentBlocks"/> is empty or contains a null element. An empty block list
    /// would produce a call that looks exactly like a document-carrying one and shows the model
    /// nothing, which is the failure this whole type exists to make impossible.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The list is copied and wrapped, for the reason <see cref="Documents.DocumentContent"/> gives:
    /// an <c>AIContent[]</c> handed out as <see cref="IReadOnlyList{T}"/> can be cast straight back to
    /// an array and written through, and one prepared document is shared by the concurrent section
    /// fan-out. The <em>blocks</em> are still shared and still mutable - they are read here, never
    /// written.
    /// </para>
    /// <para>
    /// No run options accompany this path, deliberately. The document is in the message, so there is
    /// nothing per-call to assemble and nothing a holder could null out between calls.
    /// </para>
    /// </remarks>
    public BoundDocumentAgent(AIAgent agent, IReadOnlyList<AIContent> documentBlocks)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(documentBlocks);

        if (documentBlocks.Count == 0)
        {
            throw new ArgumentException(
                "A document-bound agent needs at least one content block: binding an empty document "
                    + "produces a call that reads as document-carrying and shows the model nothing.",
                nameof(documentBlocks));
        }

        AIContent[] copy = [.. documentBlocks];
        for (int index = 0; index < copy.Length; index++)
        {
            if (copy[index] is null)
            {
                throw new ArgumentException(
                    $"Document block {index} is null; a null block would be dropped from the outbound turn "
                        + "without anything reporting it.",
                    nameof(documentBlocks));
            }

            // THE GUARD THAT MAKES THIS PATH SAFE, and it is the one thing this constructor checks
            // about the blocks themselves. A block whose runtime type is exactly AIContent carries no
            // neutral payload at all - no text, no data, no URI - so there is nothing for a chat turn
            // to express: everything it holds is in RawRepresentation, which is precisely the shape MAF
            // was measured silently DROPPING. That is what the native-PDF profile produces
            // (`new AIContent { RawRepresentation = block }`), and it must reach the model through its
            // own provider's request shaping, never through here.
            //
            // Measured, and the reason this is a constructor check rather than a comment: without it,
            // native-profile content handed to the chat-attached path passed every other check, MAF
            // dropped the block, the model answered confidently about a document it had never seen, and
            // triage reported a profile ABOVE the review threshold. Nothing failed, nothing was logged,
            // and no human was ever asked. An exact type comparison rather than a pattern, because
            // every DERIVED type (TextContent, DataContent, UriContent, and any the framework adds)
            // does have a neutral payload and is legitimate here.
            if (copy[index].GetType() == typeof(AIContent))
            {
                throw new ArgumentException(
                    $"Document block {index} carries no neutral content - its whole payload is a provider "
                        + "raw representation, which a chat turn cannot express and which MAF's provider "
                        + "integrations were measured to drop silently. Content prepared by a provider's own "
                        + "profile must be attached through that provider's request shaping; binding it here "
                        + "would send a request with no document in it and the model would answer anyway.",
                    nameof(documentBlocks));
            }
        }

        Agent = agent;
        _documentBlocks = new ReadOnlyCollection<AIContent>(copy);
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
    /// <para>
    /// On the provider-attached path the options are assembled here, per call, by the factory this
    /// binding was created with. That is the only supported way to reach the model with the document
    /// attached, and the reason is on the type: every alternative shape that has been tried could be
    /// mis-assembled without any error being raised.
    /// </para>
    /// <para>
    /// On the chat-attached path the turn is assembled here instead: the document's blocks first,
    /// then the prompt, in one user message. Same guarantee, same reason - a caller cannot send this
    /// binding's agent a turn without its document, because it never gets the chance to build one.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="message"/> is <see langword="null"/>, empty, or white space.</exception>
    public Task<AgentResponse> RunAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        if (_documentBlocks is { } blocks)
        {
            // A fresh list per call, referencing the shared blocks rather than copying them - which is
            // exactly what IDocumentContentProvider tells a consumer needing a variation to do.
            List<AIContent> turn = new(blocks.Count + 1);
            turn.AddRange(blocks);
            turn.Add(new TextContent(message));

            return Agent.RunAsync(
                new ChatMessage(ChatRole.User, turn),
                options: null,
                cancellationToken: cancellationToken);
        }

        return Agent.RunAsync(message, options: CreateRunOptions(), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Builds a fresh set of run options carrying the document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Private, and it was worth the argument to make it so.</b> It was briefly <c>internal</c>,
    /// with an <c>InternalsVisibleTo</c> on <c>Cbix.Core</c> so the wire-level tests could assert what
    /// it produced. That grant exposed <em>every</em> internal in Core - the containment helpers, the
    /// link-count reader, the secret-source types - to buy one accessor, and it bought nothing the
    /// tests could not get otherwise: the property that matters ("each call carries its own options,
    /// and no holder can break the next call") is observable through <see cref="RunAsync"/> against a
    /// recording chat client, which is also how it is observable in production. Asserting through the
    /// public path made the tests stronger and the grant unnecessary.
    /// </para>
    /// <para>
    /// Private also states the guarantee more completely than internal did: there is now no way at all
    /// to obtain these options, so the "nothing outside this type ever holds them" claim is enforced by
    /// the compiler rather than by everyone remembering not to.
    /// </para>
    /// </remarks>
    private AgentRunOptions CreateRunOptions() =>
        _runOptionsFactory?.Invoke()
            ?? throw new InvalidOperationException(
                "This binding carries its document as chat content and has no run-options factory; "
                    + "reaching here means the two transport paths were confused.");
}
