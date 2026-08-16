namespace Cbix.Core.Secrets;

/// <summary>
/// An <see cref="ISecretResolver"/> over an ordered list of <see cref="SecretSource"/>: the first
/// source carrying a value wins.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the local and CI stand-in for the bank's secrets manager</b> (see
/// <see cref="ISecretResolver"/> for the full statement of what that does and does not mean). It
/// resolves from whatever sources the composition root hands it - user-secrets, environment
/// variables, an in-memory source in tests - and it is the piece a real Vault/CyberArk provider
/// replaces or joins.
/// </para>
/// <para>
/// <b>Explicit precedence, because <c>IConfiguration</c> cannot express this one.</b>
/// Configuration's own rule is last-provider-wins for the <em>same</em> key. The sources here
/// deliberately read <em>different</em> names - a CBIX-scoped configuration key and a vendor-wide
/// environment variable are not the same key - so their relative order is a decision this type
/// makes explicit rather than a side effect of provider registration order. The order is the
/// constructor's argument order, and <see cref="SourceDescriptions"/> reports it, so a startup
/// failure tells an operator not only where to put the secret but which place takes priority.
/// </para>
/// <para>
/// <b>Blank counts as absent.</b> An empty or whitespace-only value - an environment variable
/// exported with no value, a configuration entry left as <c>""</c> - is treated as "this source
/// does not carry it" and resolution falls through to the next source. Accepting it would carry a
/// blank credential all the way to a 401 on the first API call, mid-run and far from the cause;
/// falling through instead means the operator's actual intent (the next source) still works, and if
/// nothing else carries it the failure names every source at startup.
/// </para>
/// </remarks>
public sealed class LayeredSecretResolver : ISecretResolver
{
    private readonly SecretSource[] _sources;

    /// <summary>Initialises a resolver over the given sources, highest precedence first.</summary>
    /// <param name="sources">
    /// The sources to consult, in precedence order. Must contain at least one: a resolver over no
    /// sources would answer "not configured" for everything, which looks exactly like a correctly
    /// configured deployment whose secret is missing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sources"/> is empty or contains a <see langword="null"/> entry.
    /// </exception>
    public LayeredSecretResolver(IEnumerable<SecretSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        _sources = [.. sources];

        if (_sources.Length == 0)
        {
            throw new ArgumentException(
                "A secret resolver needs at least one source; one over no sources reports every secret as "
                    + "absent, which is indistinguishable from a real misconfiguration.",
                nameof(sources));
        }

        if (Array.Exists(_sources, source => source is null))
        {
            throw new ArgumentException("A secret source must not be null.", nameof(sources));
        }

        SourceDescriptions = [.. _sources.Select(source => source.Description)];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SourceDescriptions { get; }

    /// <inheritdoc />
    public string? TryResolve(string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        foreach (SecretSource source in _sources)
        {
            string? value = source.Lookup(secretName);

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
