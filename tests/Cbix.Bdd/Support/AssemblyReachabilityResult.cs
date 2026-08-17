namespace Cbix.Bdd.Support;

/// <summary>
/// What an <see cref="AssemblyReachability"/> walk reached: every assembly name, its distance from
/// the nearest root, and the names whose own references could not be explored.
/// </summary>
/// <remarks>
/// Distances are kept rather than a flat set because the control scenario's claim is about
/// <em>how</em> the provider was reached: a walk that only ever reported direct references would
/// satisfy a set-membership assertion while missing the entire class of defect the gate exists for.
/// </remarks>
public sealed class AssemblyReachabilityResult
{
    private readonly Dictionary<string, int> _distances;

    /// <summary>Initialises a new <see cref="AssemblyReachabilityResult"/>.</summary>
    /// <param name="roots">The names the walk started from, in the order given, duplicates included.</param>
    /// <param name="distances">Every reached assembly name, mapped to its distance from the nearest root.</param>
    /// <param name="unexplored">Reached names whose own references could not be read.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AssemblyReachabilityResult(
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, int> distances,
        IReadOnlyList<string> unexplored)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(distances);
        ArgumentNullException.ThrowIfNull(unexplored);

        Roots = roots;
        Unexplored = unexplored;
        _distances = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, int> entry in distances)
        {
            _distances[entry.Key] = entry.Value;
        }
    }

    /// <summary>Gets the names the walk started from.</summary>
    public IReadOnlyList<string> Roots { get; }

    /// <summary>Gets the reached names whose own references could not be read.</summary>
    /// <remarks>
    /// Reported rather than swallowed: a walk that silently stopped early would answer "nothing
    /// banned was reachable" from a graph it had barely seen.
    /// </remarks>
    public IReadOnlyList<string> Unexplored { get; }

    /// <summary>Gets every assembly name the walk reached, roots included.</summary>
    public IReadOnlyCollection<string> Reached => _distances.Keys;

    /// <summary>Gets the distance from the nearest root, or <see langword="null"/> if unreached.</summary>
    /// <param name="assemblyName">The simple assembly name.</param>
    /// <returns>Zero for a root, one for a direct reference of a root, and so on.</returns>
    public int? DistanceTo(string assemblyName) =>
        _distances.TryGetValue(assemblyName, out int distance) ? distance : null;

    /// <summary>Gets the reached names matching a predicate, ordered for a stable failure message.</summary>
    /// <param name="predicate">The test applied to each reached name.</param>
    /// <returns>The matching names with their distances, formatted for a diagnostic.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<string> Describe(Func<string, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return
        [
            .. _distances
                .Where(entry => predicate(entry.Key))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key} (distance {entry.Value})"),
        ];
    }
}
