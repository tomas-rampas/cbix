using Cbix.Core.Documents;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/ClaudeDocumentContentProfile.feature</c> (story S01-05).
/// </summary>
/// <remarks>
/// <para>
/// The profile itself is reached by reflection, so nothing here is typed as an adapter class: the
/// BDD working agreement requires the red phase to be a failing assertion ("no such type"), not a
/// compile error that stops the whole test project building. The port's own types
/// (<see cref="DocumentReference"/>, <see cref="DocumentContent"/>) are referenced directly because
/// they live in <c>Cbix.Core</c>, which this project already depends on - and because the scenario's
/// claim is precisely that the Claude profile is reachable through the neutral port.
/// </para>
/// <para>
/// Provider payloads are never named here either. The document block the profile produces is an
/// Anthropic SDK type; the scenario inspects it as JSON, which is both the honest description of
/// what will go on the wire and the only way to assert on it without importing the leak the design
/// forbids.
/// </para>
/// </remarks>
public sealed class ClaudeDocumentContentProfileState : IDisposable
{
    private bool _disposed;

    /// <summary>Gets or sets the absolute path of the specimen on disk.</summary>
    public string? SpecimenPath { get; set; }

    /// <summary>Gets or sets the registry reference the profile is asked to prepare.</summary>
    public DocumentReference? Document { get; set; }

    /// <summary>Gets or sets the transport the adapter's client was built over.</summary>
    public HttpClient? Transport { get; set; }

    /// <summary>Gets or sets the handler counting Files API uploads on the wire.</summary>
    public UploadCountingHandler? Counter { get; set; }

    /// <summary>
    /// Gets or sets the canned Files API, or <see langword="null"/> in a live scenario where the
    /// real API answers instead.
    /// </summary>
    public CannedFilesApiHandler? CannedApi { get; set; }

    /// <summary>Gets or sets the agent factory that owns the Anthropic client, held as its interface.</summary>
    /// <remarks>
    /// Typed as <see cref="IDisposable"/> rather than as the factory: it is disposed here, and
    /// nothing else in this project may name an adapter type.
    /// </remarks>
    public IDisposable? Factory { get; set; }

    /// <summary>Gets or sets the profile under test, as the neutral port.</summary>
    public IDocumentContentProvider? Provider { get; set; }

    /// <summary>
    /// Gets or sets the endpoint the factory resolved from configuration.
    /// </summary>
    /// <remarks>
    /// Read off the factory so the upload assertion can compare a whole absolute URI rather than a
    /// path suffix: a suffix match is satisfied by <c>https://attacker.example/v1/files</c>, which is
    /// precisely the outcome the adapter's endpoint pinning exists to prevent.
    /// </remarks>
    public Uri? ResolvedEndpoint { get; set; }

    /// <summary>Gets or sets the content produced by the first preparation.</summary>
    public DocumentContent? FirstContent { get; set; }

    /// <summary>Gets or sets the content produced by the second preparation.</summary>
    public DocumentContent? SecondContent { get; set; }

    /// <summary>Releases the transport and the factory, in that order.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Transport first: it is the thing the client holds, and disposing the client while a
        // request is in flight is the one ordering that can surface as a spurious scenario failure.
        Transport?.Dispose();
        Factory?.Dispose();
    }
}
