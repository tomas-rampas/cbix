using System.Reflection;

namespace Cbix.Bdd.Support;

/// <summary>
/// A breadth-first walk of the transitive closure of assembly references, starting from a named set
/// of roots (story S01-09).
/// </summary>
/// <remarks>
/// <para>
/// <b>Transitive, because the interesting failure is never direct.</b> Nobody adds
/// <c>Microsoft.Agents.AI.Anthropic</c> to <c>Cbix.Core</c> by accident - that is caught by
/// <c>CoreAssemblyNeutralityTests</c>' one-hop allowlist and by review. The failure this walk exists
/// for is the second hop: a neutral-looking helper assembly, a new package, or a support project
/// that itself references the adapter, quietly making the provider reachable from a run that claims
/// not to need one.
/// </para>
/// <para>
/// <b>Reference tables, not loaded types.</b> Each hop reads <see cref="Assembly.GetReferencedAssemblies"/>,
/// which is what the compiler emitted, so the walk sees a dependency whether or not any code path in
/// this process ever executes it. It is therefore a statement about the build, not about which types
/// the CLR happened to load in this test run - the same reason the house containment tests read
/// reference tables rather than <c>AppDomain.CurrentDomain.GetAssemblies()</c>.
/// </para>
/// <para>
/// <b>Fail-closed on an unresolvable reference.</b> A referenced assembly that cannot be loaded is
/// still recorded as reachable at its distance; only its own references go unexplored. So a banned
/// assembly is reported even if it is missing from the output directory, and the count of
/// unexplored names is exposed so a scenario can say how much of the graph it did not see rather
/// than discover it as silence.
/// </para>
/// </remarks>
public static class AssemblyReachability
{
    /// <summary>Walks the closure of <paramref name="roots"/>.</summary>
    /// <param name="roots">The assemblies the walk starts from. Recorded at distance 0.</param>
    /// <returns>What the walk reached, and how far from a root it was.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="roots"/> is <see langword="null"/>.</exception>
    public static AssemblyReachabilityResult From(IEnumerable<Assembly> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        Dictionary<string, int> distances = new(StringComparer.Ordinal);
        List<string> unexplored = [];
        Queue<(Assembly Assembly, int Distance)> pending = new();
        List<string> rootNames = [];

        foreach (Assembly root in roots)
        {
            string name = root.GetName().Name ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            rootNames.Add(name);

            // TryAdd, because the root set is a union of several independently-derived lists (the
            // graph, the executors, the registrations) and they overlap heavily by design. A
            // duplicate root is not an error; re-walking one would just be waste.
            if (distances.TryAdd(name, 0))
            {
                pending.Enqueue((root, 0));
            }
        }

        while (pending.Count > 0)
        {
            (Assembly assembly, int distance) = pending.Dequeue();

            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                string name = reference.Name ?? string.Empty;

                // BFS: the first time a name is seen is by definition its shortest distance from a
                // root, so an already-recorded name is never re-recorded and never re-queued. That
                // is what keeps the distances meaningful for the control scenario, which asserts
                // that the provider was reached at distance 2 and not 1.
                if (name.Length == 0 || distances.ContainsKey(name))
                {
                    continue;
                }

                distances[name] = distance + 1;

                Assembly? loaded = TryLoad(reference);
                if (loaded is null)
                {
                    unexplored.Add(name);
                    continue;
                }

                pending.Enqueue((loaded, distance + 1));
            }
        }

        return new AssemblyReachabilityResult(rootNames, distances, unexplored);
    }

    /// <summary>
    /// Loads a referenced assembly, or returns <see langword="null"/> if it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// The three exceptions caught are the three ways a reference table entry fails to become an
    /// assembly: the file is not deployed beside the tests, it is not managed code, or the resolver
    /// rejects the identity. None of them is a reason to abandon the walk, and none of them hides a
    /// banned assembly - the name is recorded as reachable before this is called.
    /// </remarks>
    private static Assembly? TryLoad(AssemblyName reference)
    {
        try
        {
            return Assembly.Load(reference);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (FileLoadException)
        {
            return null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
