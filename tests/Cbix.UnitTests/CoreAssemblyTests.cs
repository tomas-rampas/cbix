using System.Reflection;

namespace Cbix.UnitTests;

/// <summary>
/// Scaffolding smoke test (story S01-01). Proves the unit-test project is wired to
/// <c>Cbix.Core</c> and that the core assembly is present in the test output.
/// Replace as real contracts land in later stories.
/// </summary>
public sealed class CoreAssemblyTests
{
    [Fact]
    public void CoreAssembly_IsLoadable()
    {
        Assembly core = Assembly.Load(new AssemblyName("Cbix.Core"));

        Assert.Equal("Cbix.Core", core.GetName().Name);
    }
}
