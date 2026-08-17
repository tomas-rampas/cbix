using Cbix.Core.Agents;
using Cbix.Core.Documents;

using global::Anthropic.Models.Beta;
using global::Anthropic.Models.Beta.Messages;

using Microsoft.Extensions.AI;

namespace Cbix.Providers.Anthropic;

/// <summary>
/// Turns content prepared by the Claude native-PDF profile into the request shape that actually
/// carries it to the model.
/// </summary>
/// <remarks>
/// <para>
/// Split out from <see cref="AnthropicAgentFactory"/> so the two halves of the seam read separately:
/// the factory decides <em>which model and how many tokens</em>, this decides <em>what the request
/// body looks like</em>. Both are provider-specific and both stay in this assembly.
/// </para>
/// <para>
/// <b>Betas are named here rather than at a call site</b> because they are not preferences: one
/// permits a document block to reference an uploaded file, the other accompanies the cache TTL the
/// profile marks that block with. Listing them beside the code that depends on them is what stops
/// a future edit removing "an unused header" - see <see cref="BuildRequest"/> for exactly how strong
/// the second claim is, and how strong it is not.
/// </para>
/// </remarks>
internal static class ClaudeDocumentAttachment
{
    /// <summary>The profile whose content this attachment understands.</summary>
    private const string SupportedProfile = ClaudeDocumentContentProvider.ProfileName;

    /// <summary>
    /// Extracts the Claude request blocks from prepared content, refusing anything else loudly.
    /// </summary>
    /// <param name="document">Content produced by the Claude native-PDF profile.</param>
    /// <returns>The provider blocks to place in the outbound request, in presentation order.</returns>
    /// <remarks>
    /// <b>Every refusal here is a refusal to fail silently.</b> Content from the generic-vision or
    /// text-only profile carries ordinary <see cref="AIContent"/> - text and images - with no raw
    /// representation to unwrap, and those profiles' content is meant to travel as ordinary
    /// multimodal content, not through this hatch. Attaching it here would drop it: the request would
    /// be sent, the model would answer, and nobody would learn that the document was missing. So the
    /// mismatch is an exception, thrown before any call is made.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="document"/> came from a different profile, or one of its blocks carries no
    /// Claude request block.
    /// </exception>
    internal static IReadOnlyList<BetaContentBlockParam> DescribeBlocks(DocumentContent document)
    {
        if (!string.Equals(document.Capabilities.ProfileName, SupportedProfile, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The content was prepared by profile '{document.Capabilities.ProfileName}', but only "
                    + $"'{SupportedProfile}' content can be attached to a Claude request. Content from the "
                    + "vision and text-only profiles travels as ordinary chat content; routing it through this "
                    + "hatch would silently send a request with no document in it.",
                nameof(document));
        }

        List<BetaContentBlockParam> blocks = new(document.Content.Count);


        for (int index = 0; index < document.Content.Count; index++)
        {
            if (document.Content[index].RawRepresentation is not BetaRequestDocumentBlock block)
            {
                throw new ArgumentException(
                    $"Prepared block {index} carries no Claude document block "
                        + $"(raw representation: {Describe(document.Content[index].RawRepresentation)}). Attaching "
                        + "it would produce a request the model answers without having seen the document.",
                    nameof(document));
            }

            // Defence in depth on the source, not a restatement of the profile's own invariant. A
            // document block can also carry a URL source or inline base64, and both are outbound
            // hazards this hatch must not become a route for: a URL source makes the model fetch a
            // location this process never validated, and a base64 source would ship document bytes
            // in the request body, bypassing the Files API upload whose digest was verified against
            // the registry identity. Only a file reference - an id the provider already holds - is
            // accepted here.
            if (!block.Source.TryPickBetaFileDocument(out BetaFileDocumentSource? source)
                || string.IsNullOrWhiteSpace(source.FileID))
            {
                throw new ArgumentException(
                    $"Prepared block {index} does not reference an uploaded file. Only a file source is "
                        + "attachable: a URL source would have the model fetch an unvalidated location, and an "
                        + "inline source would send document bytes that never passed the upload's integrity check.",
                    nameof(document));
            }

            blocks.Add(block);
        }

        if (blocks.Count == 0)
        {
            throw new ArgumentException(
                "The prepared content carries no blocks at all, so there is nothing to attach.",
                nameof(document));
        }

        return blocks;
    }

    /// <summary>
    /// Builds the request the integration's escape hatch hands to the transport.
    /// </summary>
    /// <param name="blocks">The document blocks, from <see cref="DescribeBlocks"/>.</param>
    /// <param name="model">The exact dated snapshot the agent was built for.</param>
    /// <param name="maxOutputTokens">The agent's per-response output cap.</param>
    /// <remarks>
    /// <para>
    /// <b>The document goes in its own leading user message, and the order is load-bearing.</b> The
    /// integration prepends whatever messages this request carries and then appends the agent's own
    /// turn, so the document lands first - which is what a prefix cache requires. Reversing it would
    /// leave the cached prefix ending before the document and make every call a miss.
    /// </para>
    /// <para>
    /// <b><paramref name="model"/> and <paramref name="maxOutputTokens"/> are required, not
    /// convenient.</b> They are <c>required</c> members of the request type, and the values set here
    /// were measured to override the agent's own chat options on the wire - so they must be the same
    /// values the agent was built with. <see cref="AnthropicAgentFactory.CreateDocumentAgent"/>
    /// resolves them once and hands them to a <see cref="BoundDocumentAgent"/> that rebuilds this
    /// request itself, which is the only arrangement in which they cannot drift.
    /// </para>
    /// <para>
    /// <b>The strength of the extended-cache-ttl claim, stated honestly.</b> It is derived from the
    /// pinned SDK's model - the TTL and the beta flag are declared together, and the SDK names the
    /// flag - not from a live call, and current API documentation describes the one-hour TTL without
    /// obviously gating it behind this beta. It is therefore sent on a safe-direction argument: an
    /// unnecessary beta header costs nothing, a missing required one fails the call, and those two
    /// errors are not symmetric. The <c>@requiresApi</c> lane in the S01-12 feature pins the real
    /// behaviour whenever anyone runs it against the live API.
    /// </para>
    /// </remarks>
    internal static MessageCreateParams BuildRequest(
        IReadOnlyList<BetaContentBlockParam> blocks,
        string model,
        int maxOutputTokens) =>
        new()
        {
            Model = model,
            MaxTokens = maxOutputTokens,

            // files-api: lets a document block name an uploaded file at all - required, and the
            // request fails without it.
            // extended-cache-ttl: accompanies the 1h cache_control TTL the profile sets. Sent on the
            // safe-direction argument documented above, not on a live measurement.
            Betas =
            [
                AnthropicBeta.FilesApi2025_04_14,
                AnthropicBeta.ExtendedCacheTtl2025_04_11,
            ],
            Messages =
            [
                new BetaMessageParam
                {
                    Role = Role.User,
                    Content = new BetaMessageParamContent([.. blocks]),
                },
            ],
        };

    /// <summary>Names an unexpected payload for an exception message without trusting its <c>ToString</c>.</summary>
    private static string Describe(object? rawRepresentation) =>
        rawRepresentation is null ? "none" : rawRepresentation.GetType().FullName ?? "unknown";
}
