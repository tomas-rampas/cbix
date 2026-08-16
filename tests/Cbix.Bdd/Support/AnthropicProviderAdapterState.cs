using System.Reflection;

namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for <c>Features/AnthropicProviderAdapter.feature</c> (story S01-08).
/// <para>
/// The scenario drives the adapter by reflection rather than by referencing it directly. That
/// is the same choice made for the S01-04 port scenario and for the same reason: the BDD working
/// agreement requires the red phase to be a failing assertion ("assembly not found"), not a
/// compile error that would stop the whole test project building.
/// </para>
/// <para>
/// Reflection is also the only honest way to assert the second scenario. "No code outside the
/// adapter references an Anthropic type" is a statement about what the compiler emitted into
/// each assembly's reference table - it cannot be observed from a typed call site.
/// </para>
/// </summary>
public sealed class AnthropicProviderAdapterState
{
    /// <summary>The dummy API key injected in-process. Construction must never need a real one.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The reflected <c>CreateAgent</c> factory method. Its <see cref="MethodInfo.ReturnType"/> is
    /// the <em>static</em> type the scenario asserts on - the returned instance's runtime type is a
    /// concrete MAF subclass and would not answer the question the story asks.
    /// </summary>
    public MethodInfo? CreateAgentMethod { get; set; }

    /// <summary>The agent instance the factory produced, proving construction succeeds offline.</summary>
    public object? CreatedAgent { get; set; }

    /// <summary>Assemblies that must stay free of provider types, keyed by simple name.</summary>
    public IReadOnlyDictionary<string, Assembly> NeutralAssemblies { get; set; } =
        new Dictionary<string, Assembly>(StringComparer.Ordinal);

    /// <summary>
    /// The referenced-assembly names emitted into each neutral assembly, keyed by that assembly's
    /// simple name. This is what the containment assertion is checked against.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReferencedAssemblies { get; set; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}
