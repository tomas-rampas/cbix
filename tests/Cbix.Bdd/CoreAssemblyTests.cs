using System.Reflection;

namespace Cbix.Bdd;

/// <summary>
/// Scaffolding smoke test (story S01-01). Proves the BDD test project builds, runs and
/// resolves <c>Cbix.Core</c>. Story S01-02 replaces this with a Reqnroll-driven
/// Gherkin scenario.
/// </summary>
public sealed class CoreAssemblyTests
{
    [Fact]
    public void CoreAssembly_IsLoadable()
    {
        Assembly core = Assembly.Load(new AssemblyName("Cbix.Core"));

        Assert.NotNull(core);
    }
}
