using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/MinimalWorkflowTopology.feature</c> (story S01-13).
/// </summary>
/// <remarks>
/// <para>
/// The container and the run scope are held rather than created per step because the scenarios are
/// about a <em>run</em>: the run scope is what bounds the document-content profile's memo, so a step
/// that quietly created a second scope would be exercising two runs while describing one.
/// </para>
/// <para>
/// Nothing here names a provider adapter type, and that is load-bearing rather than incidental - the
/// second scenario asserts on the assemblies the composed graph was built from, and it would be
/// answering a question this file had already decided if the file itself referenced one.
/// </para>
/// </remarks>
public sealed class MinimalWorkflowTopologyState : IDisposable
{
    private bool _disposed;

    /// <summary>Gets or sets the composed container: Core's graph plus a canned agent.</summary>
    public ServiceProvider? Container { get; set; }

    /// <summary>Gets or sets the scope standing for one workflow run.</summary>
    public IServiceScope? RunScope { get; set; }

    /// <summary>Gets or sets the absolute path of the specimen being submitted.</summary>
    public string? SpecimenPath { get; set; }

    /// <summary>Gets or sets the chat client behind the triage agent, which counts what it was asked.</summary>
    public CountingChatClient? ChatClient { get; set; }

    /// <summary>
    /// Gets or sets how many model calls had been made before the submission under test.
    /// </summary>
    /// <remarks>
    /// A baseline rather than a reset, because the duplicate scenario's arranging run legitimately
    /// calls the model once - that first submission is a real, complete run. Zeroing the counter
    /// would make the later assertion read as "the model was never called in this process", which is
    /// false and would hide an arranging run that had stopped working. The delta is the claim:
    /// <em>this</em> submission called nothing.
    /// </remarks>
    public int ModelCallsBeforeSubmission { get; set; }

    /// <summary>Gets the workflow events the run produced, in the order MAF raised them.</summary>
    /// <remarks>
    /// Order is the assertion in this feature - "ingest first, triage second" - so the list is
    /// append-only and never sorted. The events also carry the executor ids, which is why the run is
    /// watched as a stream rather than awaited for a result.
    /// </remarks>
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
