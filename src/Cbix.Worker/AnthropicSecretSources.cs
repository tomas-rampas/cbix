using Cbix.Core.Secrets;

using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace Cbix.Worker;

/// <summary>
/// Builds the <see cref="ISecretResolver"/> the worker resolves the Anthropic API key through, and
/// enforces which configuration providers are allowed to carry it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the local and CI stand-in for the bank's secrets manager, not the manager itself.</b>
/// Design 8 fixes the production mechanism as the house Vault/CyberArk pattern under rotation; what
/// is composed here is .NET configuration - user-secrets in local development, environment variables
/// in CI and containers, an in-memory source in tests. Those are chosen because a real vault client
/// is unusable on a laptop and in a CI container, and because the production provider arrives as one
/// more <c>IConfiguration</c> source, changing this file and nothing else. They offer no rotation,
/// no lease, no audit trail and no access policy, so they are a stand-in and not a substitute. See
/// <see cref="ISecretResolver"/> for the rotation limitation this implies.
/// </para>
/// <para>
/// <b>Both sources are read through <see cref="IConfiguration"/>, including the environment
/// variable.</b> The host builder registers the process environment as a configuration source, so
/// <c>ANTHROPIC_API_KEY</c> is reachable as a configuration key. Going through configuration rather
/// than <c>Environment.GetEnvironmentVariable</c> is what makes the composition testable at all: a
/// test can state the exact sources a scenario has, instead of inheriting whatever the developer
/// running it happens to have exported - and a test that mutated process-global environment state to
/// prove something about secret sourcing would be creating the flakiness it was written to prevent.
/// </para>
/// <para>
/// <b>The provider allowlist is the security control in this file.</b> Read
/// <see cref="ReadFromApprovedProvider"/>: it is deliberately shaped as "refuse unless recognised
/// and approved", because the alternative shape - refuse a list of known-bad providers - was
/// measured failing twice over. See that method for both measurements.
/// </para>
/// </remarks>
internal static class AnthropicSecretSources
{
    /// <summary>
    /// The vendor-wide environment variable, offered as the second source.
    /// </summary>
    /// <remarks>
    /// The same variable the Anthropic SDK and CLI tooling read, and CLAUDE.md sanctions it as the
    /// local developer channel. It is safe to honour here because the adapter was measured to ignore
    /// it once an explicit key is supplied (see the ambient-environment table on
    /// <c>AnthropicProviderOptions</c>), so reading it deliberately is the only way it can influence
    /// the run - there is no path where it silently overrides the configured key.
    /// </remarks>
    internal const string EnvironmentVariableName = "ANTHROPIC_API_KEY";

    /// <summary>
    /// File name of the user-secrets store, the one file-backed configuration provider allowed to
    /// carry a credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// User-secrets is a JSON file provider like any other; what makes it acceptable is that the
    /// file lives in the developer's profile, outside the repository and outside every build output
    /// and image layer, so it cannot be committed or shipped by accident. The framework writes this
    /// exact name.
    /// </para>
    /// <para>
    /// Matched case-insensitively to agree with the static asset scans, which the file system would
    /// otherwise make case-sensitive on Linux and case-insensitive on Windows - the same input
    /// classified two ways by platform is not a control. The residual hole is a <em>tracked</em>
    /// file that happens to be named <c>secrets.json</c>; that is closed from the other side, by
    /// both static scans including <c>secrets.json</c> in their patterns.
    /// </para>
    /// </remarks>
    private const string UserSecretsFileName = "secrets.json";

    /// <summary>
    /// Composes the resolver over the two sources, highest precedence first.
    /// </summary>
    /// <param name="configuration">The host's configuration, whose providers supply both sources.</param>
    /// <returns>A resolver whose <see cref="ISecretResolver.SourceDescriptions"/> name both sources.</returns>
    /// <remarks>
    /// <para>
    /// <b>Precedence: the CBIX-scoped configuration key beats the vendor-wide environment
    /// variable.</b> Configuration's own last-provider-wins rule cannot decide this, because the two
    /// sources read different names - so the order is stated here. The scoped key is the deliberate,
    /// deployment-specific channel: it is what user-secrets holds, what a container sets as
    /// <c>Cbix__Providers__Anthropic__ApiKey</c>, and what the bank's secrets-manager provider will
    /// populate. <c>ANTHROPIC_API_KEY</c> is a machine-wide variable shared with every other Claude
    /// tool on the box; letting it win would mean a developer's unrelated CLI credential silently
    /// displacing the one this deployment was configured with, with nothing in the logs to say so.
    /// Ambient never beats explicit.
    /// </para>
    /// <para>
    /// Within the first source, ordering among configuration providers stays configuration's own -
    /// last registered wins - restricted to the providers the allowlist approves.
    /// </para>
    /// <para>
    /// <b>The second source answers only for the Anthropic key's canonical name.</b> It is an alias
    /// for exactly one secret, and a resolver is a container-wide service: written as
    /// <c>_ =&gt; Read(...)</c> it would return the Anthropic key to a caller asking for a database
    /// connection string, which is how one credential becomes every credential. The equality check
    /// below is what stops that, and <c>WorkerCompositionTests</c> pins it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    internal static ISecretResolver CreateResolver(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new LayeredSecretResolver(
        [
            new SecretSource(
                "user-secrets (local development only), an environment variable, or the secrets-manager "
                    + $"configuration provider, under configuration key "
                    + $"'{CbixWorkerHostExtensions.AnthropicApiKeySecretName}'",
                secretName =>
                {
                    // Governance sweep of the ALIAS name, run here and not in the alias source
                    // below. See SweepAliasName for why this placement is the whole fix.
                    if (IsTheAnthropicApiKey(secretName))
                    {
                        SweepAliasName(configuration, secretName);
                    }

                    return ReadFromApprovedProvider(configuration, secretName, secretName);
                }),
            new SecretSource(
                $"the '{EnvironmentVariableName}' environment variable",
                secretName => IsTheAnthropicApiKey(secretName)
                    ? ReadFromApprovedProvider(configuration, EnvironmentVariableName, secretName)
                    : null),
        ]);
    }

    private static bool IsTheAnthropicApiKey(string secretName) =>
        string.Equals(
            secretName,
            CbixWorkerHostExtensions.AnthropicApiKeySecretName,
            StringComparison.Ordinal);

    /// <summary>
    /// Runs the provider governance sweep over the <c>ANTHROPIC_API_KEY</c> alias name, discarding
    /// whatever value it finds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all: the resolver short-circuits across sources.</b>
    /// <see cref="LayeredSecretResolver"/> returns as soon as a source answers, which is correct for
    /// <em>resolution</em> - once the scoped key is in hand there is nothing left to resolve. But the
    /// governance check is a side effect of reading, so short-circuiting also skipped it. Measured:
    /// <c>ANTHROPIC_API_KEY</c> planted in a tracked <c>appsettings.json</c> with an approved scoped
    /// key present composed cleanly and started the host - no refusal, exit 0. That is not an exotic
    /// configuration; it is the normal state of a correctly configured deployment that also happens
    /// to have a credential committed, which is precisely the case the run-time guard exists for
    /// (an operator-edited shipped config, or a baked image layer, that no static scan of this
    /// repository can see).
    /// </para>
    /// <para>
    /// <b>Why the sweep lives here rather than in the alias source.</b> It has to run on every
    /// resolution of the Anthropic key, including - especially - the resolutions the alias source
    /// never sees. The first source is the only one guaranteed to be consulted, so the sweep is
    /// attached to it and runs <em>before</em> that source reads its own key, where no early return
    /// can skip it. The alias source keeps its own read because it still has to produce the value
    /// when the scoped key is absent.
    /// </para>
    /// <para>
    /// <b>Why not simply evaluate every source unconditionally.</b> That was the obvious structural
    /// alternative and it is worse: sources are allowed to throw when their store is unreachable, so
    /// forcing all of them to run would let a vault outage fail a resolution the first source had
    /// already satisfied. Turning an availability problem into a startup failure is a real
    /// regression; this sweep touches only configuration providers that are already loaded in
    /// memory, so it cannot fail for availability reasons.
    /// </para>
    /// <para>
    /// The value is deliberately discarded. This is a check on <em>where a credential is sitting</em>,
    /// not a second way to obtain one - a credential in a forbidden location is disclosed whether or
    /// not this process would have used it.
    /// </para>
    /// </remarks>
    private static void SweepAliasName(IConfiguration configuration, string secretName) =>
        _ = ReadFromApprovedProvider(configuration, EnvironmentVariableName, secretName);

    /// <summary>
    /// Reads a configuration key, refusing the value outright if <em>any</em> provider carrying it
    /// is not on the approved list.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="configurationKey">The key to read.</param>
    /// <param name="secretName">The logical secret name, used only in refusal messages.</param>
    /// <remarks>
    /// <para>
    /// <b>Shape first, because the shape is the fix.</b> This began as "walk providers in reverse,
    /// return the first hit unless its description looks like appsettings". Two measurements killed
    /// that:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   <b>Returning at the first hit hides a shadowed credential.</b> A key sitting in a tracked
    ///   <c>appsettings.json</c> is invisible to the guard whenever an environment variable happens
    ///   to override it - which is the normal state of a correctly configured deployment. The
    ///   credential is still committed, still in every build output, still in the image; the guard
    ///   just never looked. Every provider is therefore inspected, and a refusal anywhere refuses
    ///   the whole read.
    ///   </description></item>
    ///   <item><description>
    ///   <b>A denylist of file-name fragments cannot enumerate the conventions.</b>
    ///   <c>Host.CreateApplicationBuilder</c> also loads <c>{ApplicationName}.settings.json</c> -
    ///   <c>Cbix.Worker.settings.json</c> here - at <em>higher</em> precedence than
    ///   <c>appsettings.json</c>. A key planted there passed the old guard end to end. The list of
    ///   file conventions the host may add is not ours to keep current, so the classification is
    ///   inverted: a provider must be positively recognised as approved, and anything else -
    ///   including a provider type nobody anticipated - is refused.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Approved:</b> environment variables, in-memory (tests and programmatic composition), and
    /// exactly one file provider - the user-secrets store. <b>Refused:</b> the command line, every
    /// other file provider, and any provider type this method does not recognise.
    /// </para>
    /// <para>
    /// <b>Why the command line is refused rather than allowed.</b> It is registered last and
    /// therefore outranks everything, and process arguments are world-readable in every process
    /// listing, shell history and container inspection output. It is the one channel where supplying
    /// the secret correctly still discloses it, so it is refused with the same loud failure as a
    /// tracked file.
    /// </para>
    /// <para>
    /// <b>Why unrecognised provider types are refused.</b> A wrapper such as
    /// <c>ChainedConfigurationProvider</c> reports nothing useful about what is underneath it, and
    /// was measured passing a tracked-file key straight through the old check. Failing closed makes
    /// that a startup error naming the type, so adding a provider is a deliberate act: the
    /// Vault/CyberArk provider will be added to the approved list by the story that introduces it,
    /// with a reason, rather than being silently trusted because it was unrecognised.
    /// </para>
    /// <para>
    /// <b>Descriptions are built from <c>GetType().Name</c> and, for file providers,
    /// <c>Source.Path</c>.</b> Never a provider's own <c>ToString()</c>: that is third-party output
    /// this solution does not control, so what it prints - conceivably including a value - is not a
    /// property anything here can guarantee.
    /// </para>
    /// </remarks>
    private static string? ReadFromApprovedProvider(
        IConfiguration configuration,
        string configurationKey,
        string secretName)
    {
        if (configuration is not IConfigurationRoot root)
        {
            // A non-root configuration (a bare section, a test double) cannot report provenance at
            // all. Reading it plainly is the honest fallback: refusing every non-root configuration
            // would break composition against a section without closing any hole, because a caller
            // that can hand over a section can equally hand over the value.
            return configuration[configurationKey];
        }

        IConfigurationProvider[] providers = [.. root.Providers];
        string? approvedValue = null;

        // Forward order, keeping the LAST approved hit: that reproduces configuration's own
        // last-provider-wins rule across the providers that survive the allowlist.
        foreach (IConfigurationProvider provider in providers)
        {
            if (!provider.TryGet(configurationKey, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (Refusal(provider) is { } refusal)
            {
                throw new SecretGovernanceException(secretName, Describe(provider), refusal);
            }

            approvedValue = value;
        }

        return approvedValue;
    }

    /// <summary>
    /// Returns why a provider may not carry a credential, or <see langword="null"/> if it may.
    /// </summary>
    private static string? Refusal(IConfigurationProvider provider) => provider switch
    {
        EnvironmentVariablesConfigurationProvider => null,
        MemoryConfigurationProvider => null,

        CommandLineConfigurationProvider =>
            "Command-line arguments are visible in every process listing, shell history and container "
                + "inspection output, so a credential passed there is disclosed to every local user. "
                + "Supply it through user-secrets, an environment variable, or the secrets manager.",

        FileConfigurationProvider file when IsUserSecretsStore(file) => null,

        FileConfigurationProvider =>
            "A file-backed configuration provider other than user-secrets must never carry a credential: "
                + "such files are committed, copied into every build output, and baked into container image "
                + "layers. Supply it through user-secrets, an environment variable, or the secrets manager.",

        _ =>
            "This provider type is not on the approved list for credentials, and an unrecognised provider "
                + "cannot be inspected for where its values actually came from. If it is a secrets manager, "
                + "add it to the approved list deliberately, with a reason; otherwise supply the credential "
                + "through user-secrets, an environment variable, or the secrets manager.",
    };

    private static bool IsUserSecretsStore(FileConfigurationProvider provider) =>
        provider.Source.Path is { } path
        && string.Equals(Path.GetFileName(path), UserSecretsFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Credential-free description of a provider, for a refusal message.</summary>
    private static string Describe(IConfigurationProvider provider) => provider switch
    {
        FileConfigurationProvider file => $"the configuration provider '{file.GetType().Name}' reading "
            + $"'{file.Source.Path}'",
        _ => $"the configuration provider '{provider.GetType().Name}'",
    };
}
