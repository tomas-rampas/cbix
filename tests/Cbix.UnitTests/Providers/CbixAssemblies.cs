using System.Reflection;

namespace Cbix.UnitTests.Providers;

/// <summary>
/// Discovers the solution's own assemblies at run time, so containment assertions cover every
/// project rather than a list somebody has to remember to extend.
/// </summary>
/// <remarks>
/// <para>
/// A hardcoded list was the original shape and it fails silently in exactly the situation the
/// assertions exist for: the sprint adds <c>Cbix.Persistence</c>, nobody edits the test, and the
/// new project can name Anthropic types freely while the suite stays green. Discovery inverts that
/// - a new project is covered the moment it ships alongside the tests.
/// </para>
/// <para>
/// Test assemblies are identified by their xUnit reference rather than by a name convention. A
/// convention ("ends with Tests") would miss a differently-named one; the xUnit reference is what
/// actually makes an assembly a test assembly. They are excluded because they legitimately
/// reference the adapter in order to test it.
/// </para>
/// </remarks>
internal static class CbixAssemblies
{
    private const string SolutionPrefix = "Cbix.";
    private const string ProviderPrefix = "Cbix.Providers.";

    /// <summary>
    /// Every solution assembly deployed beside the tests that must stay free of provider types:
    /// all <c>Cbix.*</c> assemblies except the provider adapters and the test assemblies.
    /// </summary>
    internal static IReadOnlyList<Assembly> Neutral()
    {
        List<Assembly> neutral =
        [
            .. Discover()
                .Where(assembly => !Name(assembly).StartsWith(ProviderPrefix, StringComparison.Ordinal))
                .Where(assembly => !IsTestAssembly(assembly))
                .OrderBy(Name, StringComparer.Ordinal),
        ];

        // Fail closed. An empty or truncated set would make every assertion below it pass while
        // inspecting nothing - the exact failure mode discovery is meant to remove.
        Assert.True(
            neutral.Count > 0,
            "No neutral Cbix assemblies were discovered next to the test binaries; the containment "
                + "assertions would be vacuous.");
        Assert.Contains(neutral, assembly => string.Equals(Name(assembly), "Cbix.Core", StringComparison.Ordinal));
        Assert.Contains(neutral, assembly => string.Equals(Name(assembly), "Cbix.Worker", StringComparison.Ordinal));

        // Both filters are asserted to actually fire. Either one silently matching nothing would
        // change the meaning of every caller: keeping the adapter in the set would make the
        // containment assertion fail permanently, and keeping the test assemblies in it would make
        // the suite fail as soon as a test named a provider type - each a confusing failure a long
        // way from its cause.
        Assert.DoesNotContain(
            neutral,
            assembly => Name(assembly).StartsWith(ProviderPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(
            neutral,
            assembly => string.Equals(Name(assembly), "Cbix.UnitTests", StringComparison.Ordinal));

        return neutral;
    }

    /// <summary>The simple name of an assembly, or the empty string.</summary>
    internal static string Name(Assembly assembly) => assembly.GetName().Name ?? string.Empty;

    /// <summary>
    /// Loads every <c>Cbix.*</c> assembly sitting in the test output directory.
    /// </summary>
    /// <remarks>
    /// The output directory rather than <c>AppDomain.CurrentDomain.GetAssemblies()</c>: the CLR
    /// loads assemblies lazily, so the loaded set depends on which tests happened to run first.
    /// What is on disk beside the tests is deterministic.
    /// </remarks>
    private static IEnumerable<Assembly> Discover()
    {
        foreach (string path in Directory.EnumerateFiles(AppContext.BaseDirectory, $"{SolutionPrefix}*.dll"))
        {
            Assembly? assembly = null;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch (BadImageFormatException)
            {
                // Not a managed assembly; nothing to inspect.
            }

            if (assembly is not null && Name(assembly).StartsWith(SolutionPrefix, StringComparison.Ordinal))
            {
                yield return assembly;
            }
        }
    }

    private static bool IsTestAssembly(Assembly assembly) =>
        Array.Exists(
            assembly.GetReferencedAssemblies(),
            reference => (reference.Name ?? string.Empty).StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
}
