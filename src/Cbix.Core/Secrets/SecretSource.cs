namespace Cbix.Core.Secrets;

/// <summary>
/// One place a secret may come from, paired with a credential-free description of that place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a delegate rather than an interface.</b> Every source this solution has - a configuration
/// key, an environment variable, later a vault path - is a single lookup function. An interface per
/// source would be one file and one class each to express one line, and, more importantly, it would
/// force whichever assembly declared it to name the configuration or vault types it wraps.
/// <c>Cbix.Core</c> must stay inside the neutral reference set, so the binding to a concrete
/// configuration system lives in the host and arrives here as a closure.
/// </para>
/// <para>
/// <b>This is a class, not a record, for the same reason
/// <c>AnthropicProviderOptions</c> is.</b> A record synthesises a <c>ToString</c> printing every
/// property; a source is one step away from the credential it fetches, and the habit of printing
/// secret-adjacent objects is what turns a near miss into a leak.
/// </para>
/// <para>
/// <b>A source that consults a fixed name must say so, and must answer only for that name.</b>
/// <see cref="Description"/> is what the startup failure shows an operator, so a source that
/// resolves an alias - the way the <c>ANTHROPIC_API_KEY</c> environment variable stands in for
/// <c>Cbix:Providers:Anthropic:ApiKey</c> - has to name that alias, or the message sends the
/// operator to a key nothing reads.
/// </para>
/// <para>
/// <b>The name check is mandatory, not cosmetic.</b> An alias source written as
/// <c>_ =&gt; ReadTheAliasedVariable()</c> ignores the requested name and therefore answers
/// <em>every</em> question with the same credential. Since a resolver is a container-wide service,
/// that turns one secret into the answer for all of them: a later component asking for a database
/// connection string would receive the Anthropic API key and send it to whatever it connects to.
/// An alias source must compare the requested name against the single name it stands in for and
/// return <see langword="null"/> for anything else.
/// </para>
/// </remarks>
public sealed class SecretSource
{
    private readonly Func<string, string?> _lookup;

    /// <summary>Initialises a source.</summary>
    /// <param name="description">
    /// Credential-free description precise enough for an operator to act on: a configuration key
    /// path, an environment variable name, a vault path. Shown verbatim in startup failures, so it
    /// must never contain a value.
    /// </param>
    /// <param name="lookup">
    /// Returns the value for a logical secret name, or <see langword="null"/> when this source does
    /// not carry it. A source that cannot answer at all - an unreachable vault, a denied lease -
    /// should throw rather than return <see langword="null"/>, so an outage is never mistaken for a
    /// missing entry.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="description"/> is null, empty, or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="lookup"/> is <see langword="null"/>.</exception>
    public SecretSource(string description, Func<string, string?> lookup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(lookup);

        Description = description;
        _lookup = lookup;
    }

    /// <summary>Gets the credential-free description of this source.</summary>
    public string Description { get; }

    /// <summary>Looks the secret up in this source.</summary>
    /// <remarks>
    /// Internal deliberately: the only supported way to read a source is through an
    /// <see cref="ISecretResolver"/>, which applies the precedence rule and the blank-is-absent
    /// rule uniformly. A public accessor would let a caller read one source directly and quietly
    /// bypass both.
    /// </remarks>
    internal string? Lookup(string secretName) => _lookup(secretName);
}
