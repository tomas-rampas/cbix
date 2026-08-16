using System.Reflection;

using Cbix.Core.Documents;

namespace Cbix.UnitTests;

/// <summary>
/// Backstop for the LLM-agnosticism constraint, complementing the BDD scenario in
/// <c>DocumentContentProviderPort.feature</c>. That scenario walks the port's property graph, which
/// is depth-bounded and sees properties only; this asserts on what the compiler actually emitted
/// into <c>Cbix.Core</c>'s assembly references, so it cannot be depth-truncated and catches a
/// provider type used anywhere in the assembly, in any member kind - a field, a local, a private
/// method, an attribute argument. Keep both: the walk gives the better diagnostic, this gives the
/// guarantee.
/// </summary>
public sealed class CoreAssemblyNeutralityTests
{
    /// <summary>
    /// Assemblies <c>Cbix.Core</c> may reference. The list is short on purpose. Adding to it is a
    /// deliberate architectural act, not a merge-time convenience: a provider SDK
    /// (<c>Microsoft.Agents.AI.Anthropic</c>, an OpenAI client, a Bedrock client) must never appear
    /// here, because Core is the assembly every other project depends on. A genuinely neutral new
    /// dependency may be added with its own justification.
    /// </summary>
    private static readonly string[] AllowedReferences =
    [
        "Microsoft.Extensions.AI.Abstractions",

        // Added deliberately for story S01-10, following the procedure this list exists to enforce:
        // the ingest containment boundary must emit a structured security event at the moment it
        // refuses a submission, because a refusal that is only an exception is invisible until
        // something happens to catch it - and the deployment control it monitors (the ingest share
        // being write-restricted, design doc Sprint 01 addendum) has no other instrument.
        // Justification against the rule above: this is the abstractions package - ILogger and
        // ILogger<T>, no sink, no provider, no transport - and it names no model provider, so it
        // cannot make Core depend on one. A logging *implementation* would still not belong here;
        // the host chooses sinks.
        "Microsoft.Extensions.Logging.Abstractions",

        "netstandard",
        "mscorlib",
    ];

    [Fact]
    public void CoreAssembly_ReferencesNothingOutsideTheNeutralSet()
    {
        Assembly core = typeof(IDocumentContentProvider).Assembly;

        List<string> offenders =
        [
            .. core.GetReferencedAssemblies()
                .Select(reference => reference.Name ?? string.Empty)
                .Where(name => !IsAllowed(name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            offenders.Count == 0,
            $"'{core.GetName().Name}' references assemblies outside the neutral set: {string.Join(", ", offenders)}. "
                + "Provider SDKs belong in a provider adapter project, never in Core.");
    }

    [Fact]
    public void CoreAssembly_StillReferencesTheNeutralAbstractions()
    {
        // Guards the guard: if Core ever stopped referencing Microsoft.Extensions.AI.Abstractions,
        // the test above would pass vacuously while the port had quietly lost its neutral currency.
        Assembly core = typeof(IDocumentContentProvider).Assembly;

        Assert.Contains(
            core.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Microsoft.Extensions.AI.Abstractions", StringComparison.Ordinal));
    }

    private static bool IsAllowed(string assemblyName)
    {
        if (assemblyName.StartsWith("System.", StringComparison.Ordinal)
            || string.Equals(assemblyName, "System", StringComparison.Ordinal))
        {
            return true;
        }

        return Array.Exists(AllowedReferences, allowed => string.Equals(allowed, assemblyName, StringComparison.Ordinal));
    }
}
