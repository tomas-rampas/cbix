namespace Cbix.Core.Secrets;

/// <summary>
/// Thrown when a secret <em>was</em> found, but in a place policy forbids it to come from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not an <see cref="InvalidOperationException"/>.</b> A resolver has three distinct
/// outcomes and they need three distinct fixes. Absence is
/// <see cref="SecretNotFoundException"/> - "configure it somewhere". An unreachable store is an
/// outage, which surfaces as whatever the store's client throws, and the fix is to restore the
/// store. This is the third: the credential exists and is usable, and the deployment is being
/// refused anyway because of <em>where</em> it was found. The fix is neither "configure it" nor
/// "fix the store" - it is "move it, and treat the old location as a disclosed credential that now
/// needs rotating". Sharing the <see cref="InvalidOperationException"/> channel with transport and
/// configuration failures meant a caller could not tell a governance refusal from an outage, and
/// only one of the two is a security event.
/// </para>
/// <para>
/// <b>It never contains the value.</b> The whole point of the refusal is that the credential is in
/// the wrong place; copying it into an exception message - which reaches logs, telemetry and the
/// review UI - would put it in a second wrong place. Only the secret's name and a description of
/// the offending source appear.
/// </para>
/// </remarks>
public sealed class SecretGovernanceException : Exception
{
    /// <summary>Initialises the exception for a secret found in a forbidden source.</summary>
    /// <param name="secretName">The logical secret name, in configuration-path form.</param>
    /// <param name="sourceDescription">
    /// Credential-free description of the offending source - for a configuration provider, its type
    /// name and, for a file provider, its path. Never a value, and never a third-party provider's
    /// own <c>ToString</c>, which is not under this solution's control.
    /// </param>
    /// <param name="reason">
    /// Why that source is forbidden and what to do instead, in a sentence an operator can act on.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="secretName"/>, <paramref name="sourceDescription"/>, or
    /// <paramref name="reason"/> is null, empty, or white space.
    /// </exception>
    public SecretGovernanceException(string secretName, string sourceDescription, string reason)
        : base(BuildMessage(secretName, sourceDescription, reason))
    {
        SecretName = secretName;
        SourceDescription = sourceDescription;
    }

    /// <summary>Gets the logical name of the secret that was found in a forbidden source.</summary>
    public string SecretName { get; }

    /// <summary>Gets the credential-free description of the offending source.</summary>
    public string SourceDescription { get; }

    private static string BuildMessage(string secretName, string sourceDescription, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return $"The secret '{secretName}' was supplied by {sourceDescription}, which is not an approved "
            + $"source. {reason} Treat the value currently in that location as disclosed: move it, then "
            + "rotate it.";
    }
}
