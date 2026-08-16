namespace Cbix.Core.Secrets;

/// <summary>
/// Turns an absent secret into a startup failure that names every source consulted.
/// </summary>
/// <remarks>
/// An extension rather than a second interface member: the fail-fast behaviour is the same for
/// every resolver - including the bank's - so letting an implementation define its own would let one
/// of them fail with a message that names sources it does not consult. Keeping
/// <see cref="ISecretResolver"/> down to "try, and describe yourself" also keeps the port trivial to
/// implement against a real vault client.
/// </remarks>
public static class SecretResolverExtensions
{
    /// <summary>
    /// Resolves a secret that the deployment cannot run without, or throws.
    /// </summary>
    /// <param name="resolver">The resolver to consult.</param>
    /// <param name="secretName">
    /// The logical secret name, in configuration-path form (for example
    /// <c>Cbix:Providers:Anthropic:ApiKey</c>).
    /// </param>
    /// <returns>The resolved value. Never logged, never placed in an exception, never checkpointed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="secretName"/> is null, empty, or white space.</exception>
    /// <exception cref="SecretNotFoundException">No configured source carries the secret.</exception>
    public static string Require(this ISecretResolver resolver, string secretName)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        return resolver.TryResolve(secretName)
            ?? throw new SecretNotFoundException(secretName, resolver.SourceDescriptions);
    }
}
