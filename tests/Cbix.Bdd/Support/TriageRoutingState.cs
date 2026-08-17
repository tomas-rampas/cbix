using Cbix.Core.Workflows;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/TriageLowConfidenceRouting.feature</c> (story S01-15).
/// </summary>
/// <remarks>
/// The routing threshold is held here rather than read at composition time because one scenario sets
/// it before the document is chosen and another after: the Gherkin reads in the order a deployment
/// is described, not in the order a container is built, so the steps record their intentions and the
/// composition happens once, when the run is about to start.
/// </remarks>
public sealed class TriageRoutingState : IDisposable
{
    private bool _disposed;

    /// <summary>Gets or sets the composed container.</summary>
    public ServiceProvider? Container { get; set; }

    /// <summary>Gets or sets the scope standing for one workflow run.</summary>
    public IServiceScope? RunScope { get; set; }

    /// <summary>Gets or sets the absolute path of the specimen being submitted.</summary>
    public string? SpecimenPath { get; set; }

    /// <summary>Gets or sets what the canned triage agent replies.</summary>
    public string? CannedReply { get; set; }

    /// <summary>Gets or sets the configured routing threshold, or <see langword="null"/> for the default.</summary>
    public TriageRoutingOptions? Routing { get; set; }

    /// <summary>Gets the workflow events the run produced, in the order MAF raised them.</summary>
    public List<WorkflowEvent> Events { get; } = [];

    /// <summary>Releases the run scope and the container, in that order.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        RunScope?.Dispose();
        Container?.Dispose();
    }
}
