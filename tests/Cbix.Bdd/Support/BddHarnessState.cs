namespace Cbix.Bdd.Support;

/// <summary>
/// Scenario-scoped state for the BDD harness feature.
/// <para>
/// This is the sanctioned pattern for passing state between steps in CBIX: a plain
/// class under <c>Support/</c>, resolved per scenario by Reqnroll's context injection
/// and constructor-injected into every binding class that needs it. Reqnroll creates
/// one instance per scenario and hands the same instance to every binding class in
/// that scenario, so state is shared where it should be and isolated where it must be.
/// </para>
/// <para>
/// Prefer this over string-keyed <c>ScenarioContext</c> lookups: the compiler checks
/// the names and types, rename refactoring works, and a typo is a build error instead
/// of a null at run time.
/// </para>
/// </summary>
public sealed class BddHarnessState
{
    /// <summary>Set by the Given step once the harness is considered wired.</summary>
    public bool HarnessReady { get; set; }

    /// <summary>Set by the When step once the sample scenario body has run.</summary>
    public bool ScenarioRan { get; set; }
}
