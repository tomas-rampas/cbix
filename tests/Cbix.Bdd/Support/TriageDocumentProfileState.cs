using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/TriageDocumentProfile.feature</c> (story S01-14).
/// </summary>
/// <remarks>
/// <para>
/// Every lane in the feature - canned, wire and live - composes the same production graph and runs
/// it once, so the state is deliberately uniform: a container, one run scope, the specimen path, and
/// the events the run raised. What differs between lanes is only what sits behind the triage agent,
/// which is exactly the claim the feature makes.
/// </para>
/// <para>
/// The disposables are tracked rather than assumed, because the wire and live lanes build an
/// adapter factory and its transports by reflection: nothing else in the scenario holds them, and an
/// undisposed key-bearing client per scenario is the leak the port's own lifetime contract warns
/// about.
/// </para>
/// </remarks>
public sealed class TriageDocumentProfileState : IDisposable
{
    private readonly List<IDisposable> _tracked = [];

    private bool _disposed;

    /// <summary>Gets or sets the composed container: Core's graph plus whatever fills the triage slot.</summary>
    public ServiceProvider? Container { get; set; }

    /// <summary>Gets or sets the scope standing for one workflow run.</summary>
    public IServiceScope? RunScope { get; set; }

    /// <summary>Gets or sets the absolute path of the specimen being submitted.</summary>
    public string? SpecimenPath { get; set; }

    /// <summary>Gets or sets the recorder for the Messages API traffic, on the wire and live lanes.</summary>
    public CapturingMessagesHandler? MessagesApi { get; set; }

    /// <summary>Gets or sets the canned Files API, on the wire lane.</summary>
    public CannedFilesApiHandler? FilesApi { get; set; }

    /// <summary>Gets or sets the endpoint every model call in this scenario went to.</summary>
    public Uri? ResolvedEndpoint { get; set; }

    /// <summary>Gets or sets a value indicating whether this scenario talks to the real API.</summary>
    public bool IsLive { get; set; }

    /// <summary>Gets the workflow events the run produced, in the order MAF raised them.</summary>
    public List<WorkflowEvent> Events { get; } = [];

    /// <summary>Registers a disposable the scenario built, so the teardown releases it.</summary>
    /// <param name="disposable">The resource, or <see langword="null"/> for convenience at call sites.</param>
    public void Track(IDisposable? disposable)
    {
        if (disposable is not null)
        {
            _tracked.Add(disposable);
        }
    }

    /// <summary>Releases the run scope, the container and everything the scenario tracked.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        RunScope?.Dispose();
        Container?.Dispose();

        foreach (IDisposable disposable in _tracked)
        {
            disposable.Dispose();
        }

        _tracked.Clear();
    }
}
