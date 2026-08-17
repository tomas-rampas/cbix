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

        // Added deliberately for story S01-06, following the same procedure. PDFtoImage is a local
        // page rasteriser - PDFium behind a managed wrapper - and it passes the same test PdfPig
        // does: MIT, entirely offline, no network call, and it names no model provider. It is also
        // provider-invariant by role, and pointedly so: the profile that uses it is the AGNOSTICISM
        // FALLBACK, the one that must work when no provider SDK is present at all. A dependency
        // whose only consumer exists to remove a vendor dependency cannot itself create one.
        //
        // One entry, and this is measured rather than assumed. PDFtoImage's own transitive closure
        // is large - SkiaSharp plus six native asset packages - but Cbix.Core emits a reference to
        // exactly one assembly of it, because the rendering code names only PDFtoImage's own types.
        // The one place that could have gone differently is RenderOptions: its positional
        // constructor takes SkiaSharp.SKColor and System.Drawing.RectangleF, so constructing it
        // positionally would have pulled SkiaSharp into this list. PdfiumPageImageRenderer builds it
        // with a `with` expression on a default instance instead, which names neither - see the
        // comment there. If that ever changes, this test is what will say so.
        "PDFtoImage",

        // Added deliberately for story S01-06 alongside PDFtoImage, and NOT as an oversight in the
        // first pass. The original entry claimed Core emitted a reference to PDFtoImage alone, which
        // was true while rendering used the per-page SavePng(Stream, ...) overload. That overload
        // re-parses the whole document once per page - quadratic work on input an outside party
        // supplies - so it was replaced by ToImagesAsync, which parses once and yields
        // SkiaSharp.SKBitmap. Encoding those to PNG names SKData and SKEncodedImageFormat too.
        // Naming SkiaSharp is therefore the price of a security fix, taken knowingly.
        //
        // It passes the same test PdfPig and PDFtoImage pass: a local, offline 2D graphics library,
        // author-signed by Microsoft Corporation, MIT, that names no model provider and makes no
        // network call.
        "SkiaSharp",

        // Added deliberately for story S01-13, and this is the largest single act of judgement this
        // list has recorded: Cbix.Core takes a dependency on Microsoft's Agent Framework itself.
        //
        // Why it is not the loophole it looks like. The rule this list enforces is that a PROVIDER
        // SDK may never appear here, and these are not provider SDKs - they are the framework every
        // provider is spoken to through. Microsoft.Agents.AI.Workflows declares WorkflowBuilder, the
        // executor model and the edge types: graph vocabulary, naming no vendor.
        // Microsoft.Agents.AI.Abstractions declares AIAgent, AgentRunOptions and AgentResponse - the
        // neutral currency design 3 says the whole solution speaks, and the same currency the
        // Anthropic adapter must widen its concrete agent to before anything else may see it. The
        // graph Core builds with them takes AIAgent instances as INPUT (keyed registrations the host
        // fills), so which provider supplies them remains a host decision.
        //
        // Why Core had to take it, rather than the host. CLAUDE.md places workflow-graph composition
        // in Core precisely so the graph the tests run is the graph production runs - S01-09's
        // agnosticism proof has to execute this topology against a stub IChatClient, and a graph
        // assembled inside the worker executable could only be reached by starting the worker.
        //
        // What is still banned, and by what. Microsoft.Agents.AI.Anthropic and the Anthropic SDK are
        // absent from this list, so this test refuses them for Core; ProviderContainmentTests refuses
        // them for EVERY non-adapter assembly, including the host. Neither of those loosened.
        //
        // Two entries, not three: Cbix.Core emits no reference to Microsoft.Agents.AI itself, because
        // it names nothing declared there (ChatClientAgent and ChatClientAgentRunOptions are the
        // adapter's business). Listing only what is actually referenced is what keeps this test
        // meaningful - if Core ever starts naming a concrete agent type, this list is what will say so.
        "Microsoft.Agents.AI.Abstractions",
        "Microsoft.Agents.AI.Workflows",

        // Added deliberately for story S01-13 alongside the two above, and required by the same
        // decision: Core owns the workflow half of the composition root, and describing registrations
        // means naming IServiceCollection, IServiceProvider and ServiceDescriptor. Abstractions only -
        // no container implementation, no hosting, no configuration binding, all of which stay on the
        // host's side of the split. It names no model provider and makes no network call, which is the
        // same test PdfPig, PDFtoImage and SkiaSharp pass.
        "Microsoft.Extensions.DependencyInjection.Abstractions",

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
    public void CoreAssembly_DoesNotReferenceTheWindowsOnlyDrawingStack()
    {
        // Closes a gap the allowlist above cannot see. IsAllowed passes ANY assembly whose name
        // starts with "System.", which is right for the BCL but means new System.* dependencies
        // co-travel invisibly - nobody reviews an addition the test never reports. That already
        // happened here: PDFtoImage's GetPageSizes returns IList<System.Drawing.SizeF>, so reading
        // page geometry for the render ceilings pulled System.Drawing.Primitives into Core without a
        // word from any test.
        //
        // System.Drawing.Primitives is harmless - SizeF and RectangleF, pure value types, no native
        // code. System.Drawing.COMMON is not: it is GDI+, it is Windows-only on .NET 7 and later
        // (it throws PlatformNotSupportedException everywhere else), and Core has to run in a
        // linux-x64 container. Arriving here it would build cleanly, pass every test on a developer's
        // Windows box, and fail in CI or production - the worst failure shape available. The
        // rasteriser is exactly the kind of code that could acquire it by accident.
        //
        // Named specifically rather than by a "no System.Drawing.*" prefix rule, because Primitives
        // is legitimately present and a prefix ban would fail on it.
        Assembly core = typeof(IDocumentContentProvider).Assembly;

        Assert.DoesNotContain(
            core.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "System.Drawing.Common", StringComparison.Ordinal));
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

    [Fact]
    public void CoreAssembly_ReferencesTheWorkflowEngine()
    {
        // Non-vacuity guard for the three entries story S01-13 added. If Core stopped referencing the
        // workflow engine - the graph moved back into the host, the composition root was inlined
        // somewhere - the allowlist would still contain the entries and the assertion above would
        // still pass, while the property those entries were justified by (the graph the tests run is
        // the graph production runs) had quietly stopped holding.
        Assembly core = typeof(IDocumentContentProvider).Assembly;

        Assert.Contains(
            core.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "Microsoft.Agents.AI.Workflows", StringComparison.Ordinal));
    }

    [Fact]
    public void AllowedReferences_NamesNoProviderSdk()
    {
        // The list's own rule, made checkable. Every entry above carries a justification in prose,
        // which is exactly the kind of control that decays: the next addition is made under time
        // pressure, the comment is copied, and nobody re-reads the paragraph at the top. This asserts
        // the one thing the prose must never be argued into permitting.
        //
        // Ordinal-contains rather than equality, deliberately. It has to catch a plausible future
        // entry as well as today's names - "Microsoft.Agents.AI.OpenAI", "Amazon.BedrockRuntime",
        // "Anthropic.Beta" - so the check is on the vendor token, not on the exact assembly identity.
        // The list names today's plausible providers, not an exhaustive taxonomy. "Groq" and "Claude"
        // are on it for a specific reason: CLAUDE.md names Groq (via the OpenAI-compatible
        // integration) as the likely second provider, and "Claude" is the product name a package might
        // use where "Anthropic" is the vendor name - so a hypothetical Microsoft.Agents.AI.Claude
        // would slip a vendor-name-only list.
        string[] providerTokens =
        [
            "Anthropic",
            "Claude",
            "OpenAI",
            "Groq",
            "Bedrock",
            "Gemini",
            "Google",
            "VertexAI",
            "Ollama",
            "Mistral",
            "Cohere",
            "Llama",
        ];

        List<string> offenders =
        [
            .. from allowed in AllowedReferences
               from token in providerTokens
               where allowed.Contains(token, StringComparison.OrdinalIgnoreCase)
               select $"{allowed} (matched '{token}')",
        ];

        Assert.True(
            offenders.Count == 0,
            $"The neutral allowlist names a provider SDK: {string.Join(", ", offenders)}. Core is the "
                + "assembly every other project depends on; a provider package here would make the "
                + "LLM-agnosticism constraint unenforceable everywhere at once. Provider types belong in a "
                + "Cbix.Providers.* adapter.");
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
