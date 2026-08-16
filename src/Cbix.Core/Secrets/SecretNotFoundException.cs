using System.Globalization;

namespace Cbix.Core.Secrets;

/// <summary>
/// Thrown when a required secret is not carried by any source an <see cref="ISecretResolver"/>
/// consults.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its job is to be thrown at startup.</b> A missing credential discovered lazily surfaces as a
/// 401 on the first model call - inside a workflow superstep, after checkpoints, on a document that
/// has already paid for other agents' calls, and with an error that says "unauthorized" rather than
/// "nobody configured this". The composition root resolves the secret eagerly so this lands before
/// the host starts, which is the only point at which the message below is actionable.
/// </para>
/// <para>
/// <b>The message is generated from the resolver's own source list</b>, not from a literal at the
/// call site. A hand-written "set ANTHROPIC_API_KEY or ..." string is correct exactly until someone
/// adds or reorders a source, after which it sends operators to a place nothing reads.
/// </para>
/// <para>
/// <b>It never contains a value.</b> Exception messages reach logs, telemetry and the review UI. The
/// only value-adjacent thing here is the secret's <em>name</em>, which is a key path, not a
/// credential.
/// </para>
/// </remarks>
public sealed class SecretNotFoundException : Exception
{
    /// <summary>Initialises the exception for a named secret and the sources that were consulted.</summary>
    /// <param name="secretName">The logical secret name, in configuration-path form.</param>
    /// <param name="sourceDescriptions">
    /// Credential-free descriptions of the consulted sources, in precedence order - normally
    /// <see cref="ISecretResolver.SourceDescriptions"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="secretName"/> is null, empty, or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="sourceDescriptions"/> is <see langword="null"/>.</exception>
    public SecretNotFoundException(string secretName, IReadOnlyList<string> sourceDescriptions)
        : base(BuildMessage(secretName, sourceDescriptions))
    {
        SecretName = secretName;
        SourceDescriptions = [.. sourceDescriptions];
    }

    /// <summary>Gets the logical name of the secret that could not be resolved.</summary>
    public string SecretName { get; }

    /// <summary>Gets the credential-free descriptions of the sources consulted, in precedence order.</summary>
    public IReadOnlyList<string> SourceDescriptions { get; }

    private static string BuildMessage(string secretName, IReadOnlyList<string> sourceDescriptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentNullException.ThrowIfNull(sourceDescriptions);

        string sources = sourceDescriptions.Count == 0
            ? "(none - the resolver was composed with no sources)"
            : string.Join(
                "; ",
                sourceDescriptions.Select(
                    (description, index) => string.Create(
                        CultureInfo.InvariantCulture,
                        $"({index + 1}) {description}")));

        return $"The secret '{secretName}' is not available from any configured source. Sources consulted, in "
            + $"precedence order: {sources}. Set it in one of them - never in appsettings*.json, a "
            + "'<application>.settings.json' file, a command-line argument (which is visible in every "
            + "process listing), a container image layer, or any other tracked file.";
    }
}
