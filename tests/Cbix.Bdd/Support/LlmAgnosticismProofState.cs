using System.Reflection;

using Cbix.Agnosticism;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/LlmAgnosticismProof.feature</c> (story S01-09).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Services"/> collection is kept alongside the built container, which the sibling
/// topology state does not do. It is not bookkeeping: the reachability walk roots partly on what the
/// run was <em>composed</em> from, and a <see cref="ServiceProvider"/> does not hand its descriptors
/// back. Keeping the collection is what lets the assertion be about registrations rather than about
/// a list of services somebody remembered to name.
/// </para>
/// <para>
/// Nothing here names a provider adapter type. That is deliberate but, on its own, worth little -
/// the assertion this state serves is about the run's dependency graph, not about the tidiness of
/// the test project.
/// </para>
/// </remarks>
public sealed class LlmAgnosticismProofState : IDisposable
{
    private bool _disposed;

    /// <summary>Gets or sets the collection the run was composed from.</summary>
    public ServiceCollection? Services { get; set; }

    /// <summary>Gets or sets the composed container: Core's graph plus the stub-backed agent.</summary>
    public ServiceProvider? Container { get; set; }

    /// <summary>Gets or sets the scope standing for one workflow run.</summary>
    public IServiceScope? RunScope { get; set; }

    /// <summary>Gets or sets the stub standing in for a model provider.</summary>
    public StubChatClient? ChatClient { get; set; }

    /// <summary>Gets or sets the absolute path of the document submitted.</summary>
    public string? DocumentPath { get; set; }

    /// <summary>Gets or sets the graph the run executed.</summary>
    public Workflow? Workflow { get; set; }

    /// <summary>Gets or sets the assemblies the reachability walk is rooted at.</summary>
    /// <remarks>
    /// Held rather than passed step to step because the root set <em>is</em> the claim: the control
    /// scenario differs from the proof only in what it roots the identical walk at. Both scenarios
    /// write it and both read it back - the control's When step walks from it, the proof's diagnostic
    /// reports how many roots produced the closure it is complaining about.
    /// </remarks>
    public IReadOnlyList<Assembly>? WalkRoots { get; set; }

    /// <summary>Gets or sets what the reachability walk found.</summary>
    public AssemblyReachabilityResult? Reachability { get; set; }

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
