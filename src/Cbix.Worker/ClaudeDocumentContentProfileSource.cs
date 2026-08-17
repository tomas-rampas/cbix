using Cbix.Core.Documents;

using Cbix.Providers.Anthropic;

namespace Cbix.Worker;

/// <summary>
/// Supplies the native-document profile - Claude's PDF mode over the Files API - for one workflow run
/// (story S01-05, composed by S01-13).
/// </summary>
/// <remarks>
/// <para>
/// <b>It lives in the host because only the host may choose a provider.</b> Creating this profile
/// means going through <see cref="AnthropicAgentFactory"/>, which holds the key-bearing client and
/// never exposes it. <c>Cbix.Core</c> is forbidden from referencing a provider adapter - by
/// <c>CoreAssemblyNeutralityTests</c>' closed allowlist and by
/// <c>ProviderContainmentTests.CoreAssembly_StillDoesNotKnowTheAdapterExists</c>, independently - so
/// this is the only place the native profile can be contributed from. Core's two local profiles
/// (generic-vision, text-only) need no provider and are registered there.
/// </para>
/// <para>
/// <b>It names no Anthropic type.</b> <see cref="AnthropicAgentFactory"/> is a <c>Cbix.Providers.*</c>
/// type, and everything it hands back here is Core's <see cref="IDocumentContentProvider"/> port. That
/// distinction is what <c>ProviderContainmentTests</c> asserts on this assembly's emitted reference
/// table: a host may reference the adapter project, and fails the moment a line of its code names a
/// type from <c>Anthropic</c> or <c>Microsoft.Agents.AI.Anthropic</c>.
/// </para>
/// </remarks>
internal sealed class ClaudeDocumentContentProfileSource : IDocumentContentProfileSource
{
    /// <inheritdoc/>
    public DocumentPresentationCapability Capability => DocumentPresentationCapability.NativeDocument;

    /// <inheritdoc/>
    /// <remarks>
    /// The factory is a host-lifetime singleton and the profile it creates is run-scoped; that
    /// asymmetry is the port's contract, not an oversight. The factory owns one pooled HTTP client
    /// across every run, while the profile owns the "upload each document once" memo, which must die
    /// with the run that filled it.
    /// </remarks>
    public IDocumentContentProvider Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.GetRequiredService<AnthropicAgentFactory>().CreateDocumentContentProvider();
    }
}
