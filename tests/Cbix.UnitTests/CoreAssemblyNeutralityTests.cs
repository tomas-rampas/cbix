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

        // Added deliberately for story S01-11, following the same procedure. PdfPig is a neutral
        // local document library, not a provider SDK: Apache-2.0, entirely offline, it names no
        // model provider and makes no network call. It is also provider-invariant by role - the
        // local text layer is the grounding corpus of design 5.6 and is needed identically under
        // every capability profile (Claude native-PDF, generic vision, text-only), so it can never
        // become the thing that ties Core to one vendor.
        //
        // Three entries, not one, and none is the package id: the PdfPig package ships seven
        // assemblies, and these are the ones Cbix.Core actually emits a reference to. Listing only
        // what is referenced is what keeps this test meaningful; each addition names why the code
        // started needing it.
        //
        //   UglyToad.PdfPig                        - the reader itself: PdfDocument, Page.
        //   UglyToad.PdfPig.DocumentLayoutAnalysis - ContentOrderTextExtractor, the reading-order
        //                                            text extraction the grounding corpus is built
        //                                            from.
        //   UglyToad.PdfPig.Tokens                 - NameToken, needed to interrogate the document
        //                                            catalogue for a /PageLabels number tree. The
        //                                            catalogue is a token dictionary, so asking it a
        //                                            question means naming a token type; looking the
        //                                            key up as a string would depend on how PdfPig
        //                                            happens to render tokens today.
        "UglyToad.PdfPig",
        "UglyToad.PdfPig.DocumentLayoutAnalysis",
        "UglyToad.PdfPig.Tokens",

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
