using Cbix.Core.Documents;
using Cbix.Core.Hosting;
using Cbix.Core.Ingest;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Cbix.Agnosticism;

/// <summary>
/// Composes the production CBIX workflow with a stub chat client in the triage slot: the whole
/// pipeline, no provider package (design 3, story S01-09).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the host's job, done without a host.</b> CLAUDE.md splits the composition root -
/// <see cref="CbixCoreServiceCollectionExtensions.AddCbixWorkflow"/> wires everything choosable
/// without knowing the provider, and the host supplies the <see cref="AIAgent"/> instances and the
/// native-document profile source. This type plays the host's part with the cheapest possible
/// provider: an <c>IChatClient</c> that answers from a string. Production's
/// <c>AddCbixWorker</c> makes the same two decisions against Anthropic instead, which is the sense
/// in which a provider swap is a configuration change.
/// </para>
/// <para>
/// <b>The graph is the real one.</b> Nothing here re-declares nodes or edges; it calls the same
/// registration method the worker calls, so the topology exercised offline is the topology
/// production runs. A parallel test graph would make every scenario built on it a statement about
/// the test.
/// </para>
/// <para>
/// <b>Why it registers into a collection instead of building a container.</b> Container
/// construction, logging sinks and scope ownership are the caller's, exactly as they are the host's
/// in production - and keeping them out lets this assembly depend on
/// <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> alone. That matters here more than
/// it usually would: this assembly sits inside the dependency closure S01-09 walks, so every
/// package it takes on becomes part of the claim being proved.
/// </para>
/// </remarks>
public static class StubWorkflowComposition
{
    /// <summary>
    /// Registers the CBIX workflow with <paramref name="chatClient"/> behind the triage agent.
    /// </summary>
    /// <param name="services">The container to register into. The caller owns building it.</param>
    /// <param name="ingestRoot">
    /// Fully qualified directory documents may be read from. The scenarios point it at the
    /// repository's <c>data</c> directory so the containment gate, the hashing and the PDFPig text
    /// layer all run against a real specimen rather than a fixture built to be easy.
    /// </param>
    /// <param name="chatClient">The stub standing in for a provider.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddStubBackedCbixWorkflow(
        this IServiceCollection services,
        string ingestRoot,
        StubChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(chatClient);

        services.AddCbixWorkflow(
            // The size bound is the one production uses. It is named for the Claude Files API
            // because that is the ceiling it was derived from, not because anything here talks to
            // it: DocumentIngestOptions is a Cbix.Core type and this path uploads nothing at all.
            // Taking the same number keeps the ingest gate the scenarios exercise identical to the
            // deployed one.
            new DocumentIngestOptions(ingestRoot, DocumentIngestOptions.ClaudeFilesApiLimitBytes),

            // TEXT-ONLY, and this is a decision rather than a convenience. A capability profile is
            // how the document reaches the model (design 5.1), and the two richer profiles are the
            // native-PDF one - which only a provider adapter can supply - and the generic-vision
            // one, which rasterises pages locally. Text-only is the profile that presents a
            // document with no provider present and no rendering, so it is the one an
            // agnosticism run must be able to complete under. Which profile a given capability
            // selects is proved elsewhere (the composition tests); this fixes the capability, not
            // the mechanism.
            new DocumentPresentationOptions(DocumentPresentationCapability.TextOnly));

        // The seam the whole story turns on. AddCbixWorkflow deliberately leaves the triage agent
        // unregistered because constructing one means choosing a provider; the host fills it under
        // this key. Here it is filled with ChatClientAgent - MAF's neutral adapter from an
        // IChatClient to an AIAgent - which is the same concrete type Anthropic's AsAIAgent returns.
        // The graph therefore cannot tell the two apart, and nothing downstream needed changing to
        // accept this one.
        //
        // Singleton, matching the lifetime the host gives a real agent: an agent holds a client, not
        // per-document state. It also keeps the request count on the stub cumulative across the runs
        // in one scenario, which the duplicate-submission assertions read as a baseline and a delta.
        //
        // ONE AGENT PER MODEL-CALLING NODE, and the section slot joined the list with story S01-16.
        // Both resolve to the same stub client on purpose: the agnosticism claim is about the
        // pipeline running with no provider on the path, not about the fake being elaborate. What
        // distinguishes the two calls is the ORDER the stub answers in, which is the scenario's to
        // describe - see StubChatClient's sequenced constructor.
        foreach (string node in (string[])[CbixWorkflowNodes.Triage, CbixWorkflowNodes.SectionExtraction])
        {
            services.AddKeyedSingleton<AIAgent>(
                node,
                (_, key) => new ChatClientAgent(chatClient, name: (string)key!));
        }

        return services;
    }
}
