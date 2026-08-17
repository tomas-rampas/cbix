using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Cbix.Agnosticism;
using Cbix.Bdd.Support;
using Cbix.Core.Documents;
using Cbix.Core.Extraction;
using Cbix.Core.Hosting;
using Cbix.Core.Ingest;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/DocControlExtraction.feature</c> (story S01-16).
/// </summary>
/// <remarks>
/// <para>
/// <b>The grounding assertion is the feature's centre of gravity, so it is implemented once and used
/// twice.</b> <see cref="Ground"/> walks every envelope in the section, looks its snippet up in the
/// real PDFPig text layer on the page the envelope names, and reports what did not match. The happy
/// scenario asserts the report is empty; the negative control asserts it names exactly the fabricated
/// snippet. A containment check only asserted in the positive direction is a check nobody has ever
/// seen fail.
/// </para>
/// <para>
/// <b>The story's new surface is reached by reflection</b>, for the reason the S01-12 and S01-14
/// bindings record: <c>DocControlSection</c> and <c>ExtractedField&lt;T&gt;</c> do not exist when the
/// scenarios are first written, and a red phase must be a failing assertion naming the missing
/// behaviour rather than a build break that stops the project and names nothing.
/// </para>
/// </remarks>
[Binding]
public sealed class DocControlExtractionSteps(DocControlExtractionState state)
{
    private const string ProviderAssemblyName = "Cbix.Providers.Anthropic";

    private const string DummyApiKey = "doccontrol-scenario-placeholder-key-not-a-real-credential";

    private const string LiveKeyVariable = "ANTHROPIC_API_KEY";

    /// <summary>The envelope members design 5.4 declares, in its order.</summary>
    private static readonly string[] EnvelopeMembers = ["Value", "SourcePage", "SourceSnippet", "Confidence"];

    /// <summary>
    /// The snippet the negative control fabricates: a real one with three digits changed.
    /// </summary>
    /// <remarks>
    /// Deliberately <em>nearly</em> right. A wildly wrong snippet would be caught by any check at all,
    /// including one comparing lengths or first characters; the failure grounding actually defends
    /// against is a model that paraphrases or transposes a value it did see, and this is that shape.
    /// </remarks>
    private const string FabricatedDocRefSnippet = "Document Reference CBTI-DE-2026-999";

    private static string? _liveApiKey;

    [BeforeTestRun]
    public static void CaptureLiveApiKeyForDocControl() =>
        _liveApiKey = Environment.GetEnvironmentVariable(LiveKeyVariable);

    [AfterScenario]
    public void DisposeScenarioResources() => state.Dispose();

    [Given("the DE specimen's document handle and DocumentProfile from triage")]
    public void GivenTheDeSpecimenAndItsProfile() => ComposeCanned(CannedDeSpecimenReplies.DocControlSection());

    [Given("a DocControl reply whose DocRef snippet is not in the document")]
    public void GivenAFabricatedSnippet() =>
        ComposeCanned(CannedDeSpecimenReplies.DocControlSection(FabricatedDocRefSnippet));

    [Given("a DocControl reply that is prose rather than the contract")]
    public void GivenAProseReply() =>
        ComposeCanned("The document control block says the reference is CBTI-DE-2026-011.");

    /// <summary>
    /// A reply in which every envelope is individually perfect and the section as a whole is useless.
    /// </summary>
    /// <remarks>
    /// The shape no per-field check can catch: a null value with honest null provenance is exactly what
    /// the prompting rules ask for when a document does not state something, so every envelope here is
    /// well formed. What is wrong is the section - it carries no <c>doc_ref</c> and no
    /// <c>doc_version</c>, which are <c>NOT NULL</c> and together the primary key of design 6's
    /// <c>country_documents</c>, so it cannot be persisted at all. A run reporting <c>Persisted</c> for
    /// it would be claiming an outcome the schema forbids. It is the reply a model produces when it was
    /// shown no document.
    /// </remarks>
    [Given("a DocControl reply whose every field is a well-formed null")]
    public void GivenAnAllNullReply()
    {
        JsonObject section = [];

        foreach (string field in DocControlSectionParser.ContractFields)
        {
            section[field] = new JsonObject
            {
                ["Value"] = null,
                ["SourcePage"] = null,
                ["SourceSnippet"] = null,
                ["Confidence"] = 0.05d,
            };
        }

        ComposeCanned(section.ToJsonString());
    }

    [Given("the DE specimen prepared for a Claude-backed DocControl run")]
    public void GivenAClaudeBackedRun() => ComposeClaudeBacked(live: false);

    [Given("the DE specimen prepared for a live Claude-backed DocControl run")]
    public void GivenALiveClaudeBackedRun() => ComposeClaudeBacked(live: true);

    [When("the DocControl agent runs")]
    public async Task WhenTheDocControlAgentRuns()
    {
        Workflow workflow = RunScope.ServiceProvider.GetRequiredService<CbixWorkflowFactory>().Build();

        StreamingRun run = await InProcessExecution.RunStreamingAsync(
            workflow,
            new DocumentSubmission(state.SpecimenPath!));

        await foreach (WorkflowEvent workflowEvent in run.WatchStreamAsync())
        {
            state.Events.Add(workflowEvent);
        }
    }

    [Then("it returns a DocControlSection with a non-null DocRef and Version")]
    public void ThenItReturnsASectionWithDocRefAndVersion()
    {
        object section = RequireSection();

        Assert.False(
            string.IsNullOrWhiteSpace(ValueOf(section, "DocRef") as string),
            "The DocControl section carries no document reference. It is half of the published key "
                + "(doc_ref, doc_version) in design 6, so a section without it cannot be persisted at all.");

        Assert.False(
            string.IsNullOrWhiteSpace(ValueOf(section, "Version") as string),
            "The DocControl section carries no version. It is the other half of the published key, and "
                + "the effective-dating in design 5.8 is keyed on it.");
    }

    [Then("every scalar field is wrapped in ExtractedField<T> with SourcePage and SourceSnippet populated")]
    public void ThenEveryScalarIsEnveloped()
    {
        object section = RequireSection();

        IReadOnlyList<PropertyInfo> fields = EnvelopeProperties(section);

        Assert.True(
            fields.Count >= 2,
            $"The DocControl section exposes {fields.Count} enveloped fields; design 5.4 wraps EVERY "
                + "scalar, and design 6's country_documents names the document-control block's columns.");

        foreach (PropertyInfo field in fields)
        {
            object envelope = field.GetValue(section)
                ?? throw new InvalidOperationException(
                    $"'DocControlSection.{field.Name}' is null. The envelope itself is always present - it is "
                        + "the ABSENT VALUE that is expressed as a null Value inside it, which is what keeps "
                        + "the provenance of an absence recordable.");

            foreach (string member in EnvelopeMembers)
            {
                Assert.True(
                    envelope.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance) is not null,
                    $"'ExtractedField<T>' declares no '{member}'; design 5.4's envelope is "
                        + $"({string.Join(", ", EnvelopeMembers)}), and Sprint 02's grounding gate consumes it "
                        + "unchanged.");
            }

            // Populated, not merely declared. A section whose envelopes all carried null provenance
            // would satisfy the shape check above and be worthless to the grounding gate.
            Assert.True(
                Read(envelope, "SourcePage") is int,
                $"'{field.Name}' carries no SourcePage, so the grounding gate has no page to look on.");
            Assert.False(
                string.IsNullOrWhiteSpace(Read(envelope, "SourceSnippet") as string),
                $"'{field.Name}' carries no SourceSnippet, so there is nothing to check against the document.");
        }
    }

    [Then("every SourceSnippet appears verbatim on its reported SourcePage of the PDFPig text layer")]
    public void ThenEverySnippetIsGrounded()
    {
        IReadOnlyList<string> ungrounded = Ground();

        Assert.True(
            ungrounded.Count == 0,
            "These snippets do not appear verbatim on the page they name: "
                + $"{string.Join("; ", ungrounded)}. Design 5.6 makes exactly this containment the "
                + "validator's grounding gate and the cheapest hallucination defence available - a snippet "
                + "that is not in the document is a value the model produced from somewhere else.");
    }

    [Then("the grounding check reports the fabricated snippet as ungrounded")]
    public void ThenTheGroundingCheckCatchesTheFabrication()
    {
        // The negative control. Without it every "all snippets are grounded" assertion in this feature
        // is equally satisfied by a containment check that always says yes - and that check would go
        // green for the rest of the project while defending nothing.
        IReadOnlyList<string> ungrounded = Ground();

        string reported = Assert.Single(ungrounded);

        Assert.Contains("DocRef", reported, StringComparison.Ordinal);
        Assert.Contains(FabricatedDocRefSnippet, reported, StringComparison.Ordinal);
    }

    [Then("every other SourceSnippet still appears verbatim on its reported SourcePage")]
    public void ThenEveryOtherSnippetIsStillGrounded()
    {
        // Guards the guard from the other side: a control that failed because the whole reply was
        // wrong would prove nothing about the check's precision. Exactly one field was fabricated, so
        // exactly one must be reported - which the step above already asserts with Assert.Single, and
        // this states the complement in the feature's own language.
        IReadOnlyList<string> ungrounded = Ground();

        Assert.DoesNotContain(ungrounded, entry => !entry.Contains("DocRef", StringComparison.Ordinal));
    }

    [Then("the run completes with one section extracted")]
    public void ThenTheRunCompletesWithOneSection()
    {
        WorkflowOutputEvent output = Assert.Single(state.Events.OfType<WorkflowOutputEvent>());
        ExtractionRunOutcome outcome = Assert.IsType<ExtractionRunOutcome>(output.Data);

        Assert.Equal(CbixWorkflowNodes.Persist, output.ExecutorId);
        Assert.Equal(ExtractionRunDisposition.Persisted, outcome.Disposition);

        // The disposition shape S01-13 fixed, now carrying a number that means something: the stub
        // reported zero because nothing had been extracted, and a run that still reported zero here
        // would mean the section reached persist without being counted.
        Assert.Equal(1, outcome.SectionCount);

        Assert.Empty(state.Events.OfType<WorkflowErrorEvent>());
    }

    [Then("the run fails with a typed section refusal")]
    public void ThenTheRunFailsWithATypedRefusal()
    {
        // Loud and unrouted, deliberately. Triage owns the review edge because triage's job is
        // routing; a section agent returning something that is not its contract is a validator
        // problem, and Sprint 02's ValidationReport plus its targeted retry edge is what turns one
        // into a re-invocation of just that agent.
        Assert.NotEmpty(state.Events.OfType<WorkflowErrorEvent>());

        Assert.DoesNotContain(CbixWorkflowNodes.Persist, InvokedExecutorIds());
        Assert.Empty(state.Events.OfType<WorkflowOutputEvent>());

        Type refusal = CoreType(
            "Cbix.Core.Extraction.SectionFormatException",
            "so a section parser has no typed refusal and a malformed reply would surface as whatever "
                + "exception happened to escape the parse");

        Assert.Contains(
            state.Events.OfType<WorkflowErrorEvent>(),
            failed => refusal.IsInstanceOfType(failed.Data) || refusal.IsInstanceOfType((failed.Data as Exception)?.InnerException));
    }

    [Then("no review-queue row is written for the section failure")]
    public void ThenNoReviewRowIsWritten()
    {
        Type port = CoreType("Cbix.Core.Review.IReviewQueue", "so nothing can record a review need");

        object queue = RunScope.ServiceProvider.GetService(port)
            ?? throw new InvalidOperationException($"'{port.FullName}' is not registered.");

        System.Collections.IList rows = (System.Collections.IList)Read(queue, "Entries")!;

        Assert.True(
            rows.Count == 0,
            "A section failure was routed to the review queue. The review edge is triage's, and a "
                + "section refusal that quietly queued a document would look like a handled outcome while "
                + "Sprint 02's retry loop - the thing that actually fixes it - never got the chance to run.");
    }

    [Then("the outbound DocControl request carries the document block referencing the uploaded {string}")]
    public void ThenTheRequestCarriesTheDocumentBlock(string expectedProperty)
    {
        Assert.Equal("file_id", expectedProperty);

        JsonElement block = FindDocumentBlock(RequestBody());

        Assert.Equal("file", block.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(
            CannedFilesApiHandler.CannedFileId,
            block.GetProperty("source").GetProperty(expectedProperty).GetString());

        // One upload for the whole run, shared by triage and DocControl. Two would mean the "prepare
        // once" memo had been lost - the run would still be correct and the bill would be wrong.
        Assert.Equal(1, RequireFilesApi().UploadCount);
    }

    [Then("the outbound DocControl request carries the {string} beta")]
    public void ThenTheRequestCarriesTheBeta(string expectedBeta)
    {
        CapturingMessagesHandler messages = RequireMessagesApi();

        Assert.True(
            messages.BetaHeader is not null,
            "The DocControl request carried no anthropic-beta header, so the document block could not "
                + "reference the uploaded file and the 1h cache TTL would be sent with no declared opt-in.");
        Assert.Contains(expectedBeta, messages.BetaHeader!, StringComparison.Ordinal);

        // The LAST request is DocControl's: triage runs first, so a recorder that only ever saw one
        // call would mean the section agent never reached the wire at all.
        Assert.Equal(2, messages.RequestCount);

        // The endpoint every call in this scenario went to, asserted rather than merely recorded - the
        // same check the triage feature makes, and for the same reason: a configured BaseUrl is a
        // blessed redirection (design 8 anticipates an egress proxy), so a scenario that never looked
        // would pass just as happily against another host.
        Uri endpoint = state.ResolvedEndpoint
            ?? throw new InvalidOperationException("The Given step did not record the resolved endpoint.");

        Assert.NotNull(messages.RequestUri);
        Assert.Equal(new Uri(endpoint, "/v1/messages?beta=true").AbsoluteUri, messages.RequestUri!.AbsoluteUri);
    }

    /// <summary>
    /// Checks every envelope's snippet against the real text layer, returning what did not match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ordinal containment, on the page the envelope names</b> - the exact check design 5.6 gives
    /// the validator, implemented here rather than borrowed from production code, so the scenario is
    /// not asserting that the pipeline agrees with itself.
    /// </para>
    /// <para>
    /// The text layer is re-extracted from the committed specimen rather than read out of run state:
    /// the claim is that the snippet is in the DOCUMENT, and a corpus taken from the same run that
    /// produced the snippet would make that claim circular if the run had ever mangled it.
    /// </para>
    /// <para>
    /// <b>The snippet is compared RAW and rendered SANITISED, and the split is the point.</b>
    /// Containment is ordinal, so the check must see the exact characters the model produced -
    /// sanitising before comparing would ground a snippet that is not in the document, and refuse one
    /// that is. But the failure message goes to a terminal and a log, where an unsanitised snippet
    /// forges lines and reorders text, so every RENDERING goes through
    /// <see cref="ExtractionText.ForMessage"/>. Sprint 02's validator must copy this split into
    /// <c>ValidationReport</c>: it compares the same strings and then quotes them into a retry prompt
    /// and a review row.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string> Ground()
    {
        object section = RequireSection();

        TextLayer textLayer = RunScope.ServiceProvider
            .GetRequiredService<ITextLayerExtractor>()
            .ExtractAsync(BuildReference()).GetAwaiter().GetResult();

        List<string> ungrounded = [];

        foreach (PropertyInfo field in EnvelopeProperties(section))
        {
            object? envelope = field.GetValue(section);
            if (envelope is null)
            {
                continue;
            }

            if (Read(envelope, "SourceSnippet") is not string snippet || string.IsNullOrEmpty(snippet))
            {
                continue;
            }

            int page = Read(envelope, "SourcePage") is int reported ? reported : -1;

            if (!textLayer.TryGetPage(page, out string? text))
            {
                ungrounded.Add($"{field.Name}: page {page} is not in the document ('{ExtractionText.ForMessage(snippet)}')");
                continue;
            }

            if (!text.Contains(snippet, StringComparison.Ordinal))
            {
                ungrounded.Add($"{field.Name}: '{ExtractionText.ForMessage(snippet)}' is not on page {page}");
            }
        }

        return ungrounded;
    }

    /// <summary>Composes the production graph with canned replies behind triage and DocControl.</summary>
    /// <remarks>
    /// Two replies, in call order: triage's profile, then the section. The stub reads neither and
    /// cannot tell the agents apart - the ordering is the scenario's description of the run, which is
    /// what keeps a failing scenario unambiguous between "the pipeline is wrong" and "the fake is
    /// clever".
    /// </remarks>
    private void ComposeCanned(string docControlReply)
    {
        ServiceCollection services = [];
        services.AddLogging();
        services.AddStubBackedCbixWorkflow(
            RepositoryLayout.DataDirectory(),
            new StubChatClient(CannedDeSpecimenReplies.TriageProfile(), docControlReply));

        Build(services);
    }

    /// <summary>Composes the production graph with the real Anthropic adapter behind both agents.</summary>
    private void ComposeClaudeBacked(bool live)
    {
        state.IsLive = live;

        CannedFilesApiHandler files = new();
        CapturingMessagesHandler messages = live
            ? new CapturingMessagesHandler(new HttpClientHandler())
            : new CapturingMessagesHandler(CannedDeSpecimenReplies.TriageProfile(), CannedDeSpecimenReplies.DocControlSection());

        state.FilesApi = files;
        state.MessagesApi = messages;

        HttpMessageHandler transportHandler = live ? messages : new ClaudeEndpointRouter(files, messages);
        state.Track(transportHandler);

        HttpClient transport = new(transportHandler, disposeHandler: false);
        state.Track(transport);

        (object factory, Type factoryType) = BuildFactory(transport, live);
        state.Track(factory as IDisposable);

        state.ResolvedEndpoint = factoryType
            .GetProperty("ResolvedEndpoint", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(factory) as Uri
            ?? throw new InvalidOperationException($"'{factoryType.FullName}' exposes no 'ResolvedEndpoint'.");

        Type seam = CoreType(
            "Cbix.Core.Agents.IDocumentBoundAgentFactory",
            "so a host has no neutral way to give a graph node an agent bound to the run's document");

        MethodInfo create = factoryType.GetMethod("CreateDocumentAgentFactory", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"'{factoryType.FullName}' exposes no 'CreateDocumentAgentFactory'.");

        ServiceCollection services = [];
        services.AddLogging();

        foreach (string node in (string[])[CbixWorkflowNodes.Triage, CbixWorkflowNodes.SectionExtraction])
        {
            object agentFactory = create.Invoke(factory, BuildAgentFactoryArguments(create, node))!;
            services.AddKeyedSingleton(seam, node, agentFactory);
        }

        services.AddSingleton<IDocumentContentProfileSource>(new ReflectedNativeDocumentSource(factory, factoryType));

        services.AddCbixWorkflow(
            new DocumentIngestOptions(RepositoryLayout.DataDirectory(), DocumentIngestOptions.ClaudeFilesApiLimitBytes),
            new DocumentPresentationOptions(DocumentPresentationCapability.NativeDocument));

        Build(services);
    }

    private void Build(ServiceCollection services)
    {
        ServiceProvider container = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        state.Container = container;
        state.RunScope = container.CreateScope();
        state.SpecimenPath = RepositoryLayout.DeSpecimenPath();
    }

    private static (object Factory, Type FactoryType) BuildFactory(HttpClient transport, bool live)
    {
        Assembly provider = Assembly.Load(new AssemblyName(ProviderAssemblyName));

        Type optionsType = provider.GetExportedTypes().Single(type => type.Name == "AnthropicProviderOptions");
        Type factoryType = provider.GetExportedTypes().Single(type => type.Name == "AnthropicAgentFactory");

        object options = Activator.CreateInstance(optionsType)!;
        optionsType.GetProperty("ApiKey", BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(options, live ? _liveApiKey : DummyApiKey);
        optionsType.GetProperty("Transport", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(options, transport);

        return (Activator.CreateInstance(factoryType, options)!, factoryType);
    }

    private static object?[] BuildAgentFactoryArguments(MethodInfo method, string node)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            arguments[index] = parameter.Name switch
            {
                "name" => node,
                "instructions" => "Extract, never interpret. Copy snippets verbatim. Use only the supplied document.",
                _ when parameter.HasDefaultValue => parameter.DefaultValue,
                _ => throw new InvalidOperationException(
                    $"'CreateDocumentAgentFactory' requires an unrecognised parameter '{parameter.Name}'."),
            };
        }

        return arguments;
    }


    private static JsonElement FindDocumentBlock(JsonElement body)
    {
        foreach (JsonElement message in body.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("content").ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement block in message.GetProperty("content").EnumerateArray())
            {
                if (block.TryGetProperty("type", out JsonElement type)
                    && string.Equals(type.GetString(), "document", StringComparison.Ordinal))
                {
                    return block;
                }
            }
        }

        Assert.Fail(
            "The outbound DocControl request carries no document block. A green extraction with no "
                + "document on the wire means the model produced those values from nothing - fluently, "
                + "plausibly, and about a document it was never shown.");
        throw new InvalidOperationException("Unreachable: Assert.Fail always throws.");
    }

    /// <summary>The section the run produced, read off its own event stream.</summary>
    private object RequireSection()
    {
        ExecutorCompletedEvent completed = Assert.Single(
            state.Events.OfType<ExecutorCompletedEvent>(),
            candidate => string.Equals(candidate.ExecutorId, CbixWorkflowNodes.SectionExtraction, StringComparison.Ordinal));

        object candidateData = completed.Data
            ?? throw new InvalidOperationException("The section node completed with no payload.");

        return Read(candidateData, "DocControl")
            ?? throw new InvalidOperationException(
                $"'{candidateData.GetType().Name}' exposes no 'DocControl', so the parsed DocControlSection "
                    + "never reaches the persist step and nothing downstream can validate it.");
    }

    /// <summary>The section's enveloped fields, in declaration order.</summary>
    private static IReadOnlyList<PropertyInfo> EnvelopeProperties(object section) =>
    [
        .. section.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType.IsGenericType
                && string.Equals(property.PropertyType.Name, "ExtractedField`1", StringComparison.Ordinal)),
    ];

    private static object? ValueOf(object section, string field)
    {
        PropertyInfo property = section.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"'DocControlSection' exposes no '{field}'; design 6's country_documents names it as part of "
                    + "the document-control block.");

        object envelope = property.GetValue(section)
            ?? throw new InvalidOperationException($"'DocControlSection.{field}' is null.");

        return Read(envelope, "Value");
    }

    private static Type CoreType(string fullName, string consequence) =>
        typeof(CbixWorkflowFactory).Assembly.GetType(fullName, throwOnError: false)
            ?? throw new InvalidOperationException($"'{fullName}' does not exist, {consequence}.");

    private static object? Read(object instance, string propertyName) =>
        instance.GetType()
            .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(instance);

    private DocumentReference BuildReference()
    {
        string path = state.SpecimenPath!;

        using FileStream stream = File.OpenRead(path);
        ContentHash hash = ContentHash.ComputeSha256Async(stream).GetAwaiter().GetResult();

        return new DocumentReference(hash.Canonical, new Uri(path), Path.GetFileName(path));
    }

    private List<string> InvokedExecutorIds() =>
        [.. state.Events.OfType<ExecutorInvokedEvent>().Select(invoked => invoked.ExecutorId)];

    private JsonElement RequestBody()
    {
        CapturingMessagesHandler messages = RequireMessagesApi();

        Assert.True(messages.RequestCount > 0, "No model request reached the transport.");
        Assert.NotNull(messages.Body);

        return JsonDocument.Parse(messages.Body!).RootElement;
    }

    private IServiceScope RunScope =>
        state.RunScope ?? throw new InvalidOperationException(
            "No workflow was composed; a Given step must run before this one.");

    private CapturingMessagesHandler RequireMessagesApi() =>
        state.MessagesApi ?? throw new InvalidOperationException("This lane records no Messages API traffic.");

    private CannedFilesApiHandler RequireFilesApi() =>
        state.FilesApi ?? throw new InvalidOperationException("This lane installs no canned Files API.");

    /// <summary>Contributes the adapter's native-document profile without naming an Anthropic type.</summary>
    private sealed class ReflectedNativeDocumentSource(object factory, Type factoryType) : IDocumentContentProfileSource
    {
        public DocumentPresentationCapability Capability => DocumentPresentationCapability.NativeDocument;

        public IDocumentContentProvider Create(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);

            MethodInfo create = factoryType.GetMethod(
                    "CreateDocumentContentProvider",
                    BindingFlags.Public | BindingFlags.Instance,
                    Type.EmptyTypes)
                ?? throw new InvalidOperationException($"'{factoryType.FullName}' exposes no 'CreateDocumentContentProvider()'.");

            return (IDocumentContentProvider?)create.Invoke(factory, [])
                ?? throw new InvalidOperationException("'CreateDocumentContentProvider' returned null.");
        }
    }
}
