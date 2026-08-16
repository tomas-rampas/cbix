using Microsoft.Extensions.Configuration;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/SecretSourcing.feature</c> (story S01-17).
/// </summary>
/// <remarks>
/// <para>
/// The scenarios drive the worker's composition by reflection rather than by calling its
/// registration extension directly - the same choice the S01-04 and S01-08 scenarios made, for the
/// same reason: the BDD working agreement requires the red phase to be a failing assertion
/// ("no registration extension found"), not a compile error that would stop the whole test project
/// building.
/// </para>
/// <para>
/// <b>Nothing here holds a real credential.</b> The placeholder below is fictional and deliberately
/// carries no <c>sk-ant-</c> vendor prefix: secret scanners pattern-match that shape, and a
/// repository that trips its own scanner on test data teaches everyone to ignore the alerts.
/// </para>
/// </remarks>
public sealed class SecretSourcingState : IDisposable
{
    /// <summary>The fictional key the secrets-manager stand-in carries during a scenario.</summary>
    public const string PlaceholderApiKey = "bdd-secret-sourcing-placeholder-not-a-real-credential";

    private readonly List<string> _temporaryDirectories = [];

    /// <summary>Files the first scenario scanned, so the scan can be proved non-vacuous.</summary>
    public IReadOnlyList<string> ScannedFiles { get; set; } = [];

    /// <summary>Credential-shaped findings from the scan, as "file: reason" strings.</summary>
    public IReadOnlyList<string> ScanFindings { get; set; } = [];

    /// <summary>
    /// Configuration sources the composition scenario supplies, in the order they are added.
    /// Later sources win, matching <see cref="IConfiguration"/>'s own semantics.
    /// </summary>
    public List<Action<IConfigurationBuilder>> ConfigurationSources { get; } = [];

    /// <summary>
    /// The source standing in for the secrets manager, kept separately so the scenario can compose
    /// a second time without it and observe the difference.
    /// </summary>
    public Action<IConfigurationBuilder>? SecretsManagerSource { get; set; }

    /// <summary>The composed host, when composition succeeded.</summary>
    public IDisposable? Host { get; set; }

    /// <summary>The service provider of <see cref="Host"/>, when composition succeeded.</summary>
    public IServiceProvider? Services { get; set; }

    /// <summary>The failure composition threw, when it failed.</summary>
    public Exception? CompositionFailure { get; set; }

    /// <summary>Creates a temporary directory that is deleted when the scenario ends.</summary>
    public string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cbix-bdd-secrets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _temporaryDirectories.Add(path);
        return path;
    }

    /// <summary>Disposes the composed host and removes any temporary files the scenario wrote.</summary>
    public void Dispose()
    {
        Host?.Dispose();
        Host = null;
        Services = null;

        foreach (string directory in _temporaryDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a scenario over; the OS reclaims it.
            }
        }

        _temporaryDirectories.Clear();
    }
}
