namespace Cbix.Core.Secrets;

/// <summary>
/// Resolves a named secret from the deployment's secrets manager.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this port is for.</b> Design 8 fixes the production mechanism - "the API key lives in the
/// bank's secrets manager under rotation", the house Vault/CyberArk pattern - but nothing in the
/// pipeline should have to know which manager that is. This is the one named seam between "the
/// worker needs a credential" and "this deployment stores credentials there", so swapping the store
/// is a composition-root change and not a change anywhere else.
/// </para>
/// <para>
/// <b>The local and CI resolver is a documented stand-in, not the production mechanism.</b> What
/// runs today is <see cref="LayeredSecretResolver"/> over .NET configuration sources: user-secrets
/// in local development, environment variables in CI, an in-memory source in tests. Those are
/// stand-ins for the bank's secrets manager, chosen because a real Vault/CyberArk client would be
/// unusable on a developer laptop and in a CI container, and because the production provider will
/// arrive as one more <c>IConfiguration</c> source behind this same interface. They are <em>not</em>
/// equivalent to it: they offer no rotation, no lease, no audit trail, and no access policy.
/// Deploying the stand-in into an environment holding a real key would be a security incident, not
/// a shortcut - which is why it is written down here rather than in a commit message.
/// </para>
/// <para>
/// <b>Neutral by construction.</b> This port names no configuration, hosting or provider type: it is
/// a string in, a string out. That keeps <c>Cbix.Core</c> inside the neutral reference set
/// (enforced by <c>CoreAssemblyNeutralityTests</c>) and leaves the binding to a concrete
/// configuration system where it belongs - in the host, which owns configuration composition.
/// </para>
/// <para>
/// <b>Values returned here are credentials.</b> A caller must not log them, put them in an exception
/// message, write them to a checkpoint, or place them in any type whose <c>ToString</c> prints its
/// properties. Only <see cref="SourceDescriptions"/> is safe to print.
/// </para>
/// <para>
/// <b>Rotation: not supported in v1, and that is a decision rather than an oversight.</b> The worker
/// resolves each secret <em>once</em>, during host composition, and hands the value to the component
/// that needs it. A key rotated in the store therefore does not reach a running process: rotation
/// requires a restart. That is acceptable for the PoC - the worker is a restartable batch host, not
/// a long-lived request server - and it buys a large simplification, because a rotating credential
/// means every consumer must re-read on failure and the adapter must be able to rebuild its client
/// mid-run. The story that introduces the real Vault/CyberArk provider must revisit this: leases
/// expire on their own schedule, so "restart to rotate" stops being a choice there.
/// </para>
/// <para>
/// <b>Scope: this is a live credential-read capability.</b> An implementation registered in the
/// container can be resolved by anything in the container and asked for any secret name it knows.
/// Two things keep that honest today: each source answers only for the names it is responsible for
/// (see <see cref="SecretSource"/>), so a resolver cannot hand out one secret in answer to a
/// question about another; and the composition root resolves what it needs eagerly and does not
/// pass the resolver into the workflow. Narrowing the registration - a keyed or per-component
/// resolver, or removing it from the container entirely once callers are known - is deferred to
/// S01-13, when the set of components that legitimately need a credential exists and can be
/// enumerated rather than guessed.
/// </para>
/// </remarks>
public interface ISecretResolver
{
    /// <summary>
    /// Gets credential-free descriptions of the sources this resolver consults, in the order it
    /// consults them.
    /// </summary>
    /// <remarks>
    /// This exists so a failure can name where the operator should put the secret without any
    /// call site restating a list that would then drift. Each description must identify a source
    /// precisely enough to act on - a configuration key path, an environment variable name, a vault
    /// path - and must never contain a value.
    /// </remarks>
    IReadOnlyList<string> SourceDescriptions { get; }

    /// <summary>
    /// Returns the value of <paramref name="secretName"/>, or <see langword="null"/> when no
    /// configured source carries it.
    /// </summary>
    /// <param name="secretName">
    /// The logical secret name, in configuration-path form (for example
    /// <c>Cbix:Providers:Anthropic:ApiKey</c>). A configuration-backed source reads it as a key; a
    /// vault-backed source maps it to a secret path.
    /// </param>
    /// <returns>The secret value, or <see langword="null"/> when no source carries it.</returns>
    /// <remarks>
    /// Returns <see langword="null"/> rather than throwing so a caller can distinguish "absent" from
    /// "the store is unreachable" - a source that cannot answer is expected to throw, and that
    /// failure must not be mistaken for a missing entry. Callers that require the secret should use
    /// <see cref="SecretResolverExtensions.Require"/>, which turns absence into a failure naming
    /// every source.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="secretName"/> is null, empty, or white space.</exception>
    string? TryResolve(string secretName);
}
