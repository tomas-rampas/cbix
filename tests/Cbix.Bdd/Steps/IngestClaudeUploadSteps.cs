using System.Reflection;
using System.Text.Json;

using Cbix.Bdd.Support;
using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/IngestClaudeDocumentUpload.feature</c> (story S01-12).
/// </summary>
/// <remarks>
/// <para>
/// The ingest service and its result are <c>Cbix.Core</c> types this project references directly,
/// but the two things this story adds to them - the content-provider collaborator and the handle on
/// the result - are reached by reflection. That is the BDD working agreement rather than fussiness:
/// a direct call to a constructor overload that does not exist yet is a build break, which stops the
/// whole test project and tells nobody which behaviour is missing.
/// </para>
/// <para>
/// Everything below the port is real: the real ingest gate, the real PDFPig extractor against the
/// real specimen, the real Claude profile, the real SDK. Only the far end of each socket is canned.
/// </para>
/// </remarks>
[Binding]
public sealed class IngestClaudeUploadSteps(IngestClaudeUploadState state)
{
    private const string ProviderAssemblyName = "Cbix.Providers.Anthropic";

    private const string SpecimenFileName = "Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf";

    private const string DummyApiKey = "bdd-scenario-placeholder-key-not-a-real-credential";

    /// <summary>The dated snapshot the Haiku-tier agents run on, as the adapter pins it.</summary>
    private const string HaikuSnapshot = "claude-haiku-4-5-20251001";

    [AfterScenario]
    public void DisposeScenarioResources() => state.Dispose();

    [Given("the registered DE specimen with its PDFPig text layer")]
    public void GivenTheRegisteredDeSpecimenWithItsTextLayer() => ArrangeIngestWithClaudeProfile();

    [Given("the DE specimen was already ingested with the Claude profile active")]
    public async Task GivenTheDeSpecimenWasAlreadyIngested()
    {
        ArrangeIngestWithClaudeProfile();

        state.FirstResult = await IngestAsync();

        Assert.True(state.FirstResult.IsNewRegistration, "The first submission should have registered the specimen.");
        Assert.Equal(1, RequireUploadCounter().UploadCount);
    }

    [Given("a run whose document handle was produced by ingest")]
    public async Task GivenARunWhoseDocumentHandleWasProducedByIngest()
    {
        ArrangeIngestWithClaudeProfile();

        state.FirstResult = await IngestAsync();

        Assert.Equal(1, RequireUploadCounter().UploadCount);
        Assert.NotNull(ContentHandleOf(state.FirstResult));
    }

    /// <summary>Arranges the same run against the real Files and Messages APIs.</summary>
    /// <remarks>
    /// <para>
    /// This is the lane that settles the one claim the offline scenarios cannot: whether the API
    /// actually requires the <c>extended-cache-ttl</c> beta that accompanies the block's 1h TTL. The
    /// offline assertions pin that we send it; only a live call can say whether omitting it would
    /// have failed, and until someone runs this the claim stays labelled as SDK-derived.
    /// </para>
    /// <para>
    /// It uploads the specimen and spends one model call. Both are the point, and both are why it is
    /// behind a tag and an environment variable.
    /// </para>
    /// </remarks>
    [Given("a live run whose document handle was produced by ingest")]
    public async Task GivenALiveRunWhoseDocumentHandleWasProducedByIngest()
    {
        state.IsLive = true;

        ArrangeIngestWithClaudeProfile();

        state.FirstResult = await IngestAsync();

        Assert.Equal(1, RequireUploadCounter().UploadCount);
        Assert.NotNull(ContentHandleOf(state.FirstResult));
    }

    [When("the ingest executor runs with the Claude profile active")]
    public async Task WhenTheIngestExecutorRuns()
    {
        state.FirstResult = await IngestAsync();
    }

    [When("the same file is submitted to the ingest executor again unchanged")]
    public async Task WhenTheSameFileIsSubmittedAgain()
    {
        state.DuplicateResult = await IngestAsync();
    }

    [When("a section agent runs against that document")]
    public async Task WhenASectionAgentRunsAgainstThatDocument()
    {
        // The handle is what a resumed run carries, so the content the agent is shown is re-obtained
        // from it rather than smuggled out of the ingest call - which is also what the workflow will
        // do after a checkpoint.
        DocumentContentHandle handle = ContentHandleOf(RequireFirstResult())
            ?? throw new InvalidOperationException("Ingest produced no document handle to attach.");

        IDocumentContentProvider provider = BuildProvider(RequireUploadTransport());
        DocumentContent content = await provider
            .PrepareAsync(await BuildReferenceAsync(), handle)
            .ConfigureAwait(false);

        state.PreparedContent = content;

        // A second factory over its own transport: this one is the agent's conversation, and mixing
        // it with the upload's would let each scenario's assertions read the other's traffic.
        CapturingMessagesHandler messages = state.IsLive
            ? new CapturingMessagesHandler(new HttpClientHandler())
            : new CapturingMessagesHandler();

        state.MessagesApi = messages;
        state.MessagesTransport = new HttpClient(messages);

        (object factory, Type factoryType) = BuildFactory(state.MessagesTransport, state.IsLive);
        state.Track(factory as IDisposable);

        state.ResolvedEndpoint = factoryType
            .GetProperty("ResolvedEndpoint", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(factory) as Uri
            ?? throw new InvalidOperationException(
                $"'{factoryType.FullName}' exposes no 'ResolvedEndpoint'; the scenario cannot check where the "
                    + "document was actually sent.");

        MethodInfo create = factoryType.GetMethod("CreateDocumentAgent", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"'{factoryType.FullName}' exposes no 'CreateDocumentAgent'; there is no seam that attaches a "
                    + "prepared document to an agent call, so the document block cannot reach the model.");

        object bound = create.Invoke(factory, BuildDocumentAgentArguments(create, content))
            ?? throw new InvalidOperationException("'CreateDocumentAgent' returned null.");

        // Run through the binding's own method, which is the only way that carries the document. The
        // options are internal and rebuilt per call precisely so no caller - including this one - can
        // assemble a request that looks right and sends no document.
        MethodInfo run = bound.GetType().GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"'{bound.GetType().FullName}' exposes no 'RunAsync'; there is no supported way to reach the "
                    + "model with the prepared document attached.");

        try
        {
            await ((Task)run.Invoke(bound, ["Extract the document control block.", CancellationToken.None])!)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Resource exhaustion propagates as itself - the codebase rule the ingest ports state.
            //
            // Otherwise recorded rather than swallowed. On the canned path the response is minimal
            // and a layer above may reject it, which is irrelevant: the handler captured the request
            // before any response was parsed, and the request is what those scenarios assert on. On
            // the LIVE path the outcome is the whole assertion, so it has to survive to a Then step
            // rather than being discarded here.
            state.AgentRunFailure = error;
        }
    }

    [Then("the Claude Files API upload happens exactly once")]
    public void ThenTheUploadHappensExactlyOnce()
    {
        UploadCountingHandler counter = RequireUploadCounter();

        Assert.True(
            counter.UploadCount == 1,
            $"The Files API saw {counter.UploadCount} uploads for one newly registered document; ingest is the "
                + "one place that knows a submission is new, so it is the one place 'once' can be guaranteed.");
    }

    [Then("the resulting file_id is attached to the run's document handle for downstream agents")]
    public void ThenTheFileIdIsAttachedToTheRunsDocumentHandle()
    {
        DocumentIngestResult result = RequireFirstResult();

        DocumentContentHandle? handle = ContentHandleOf(result);
        Assert.True(
            handle is not null,
            "The ingest result carries no document handle, so nothing downstream can reach the uploaded file.");

        Assert.Equal(result.Registered.DocumentId, handle!.DocumentId);
        Assert.Equal("claude-native-pdf", handle.ProfileName);
        Assert.Equal(CannedFilesApiHandler.CannedFileId, handle.ProviderToken);

        // The text layer still comes out of the same call: the handle is an addition to what a
        // continuing run carries, not a replacement for the grounding corpus.
        Assert.NotNull(result.TextLayer);
    }

    [Then("no further Files API upload is made")]
    public void ThenNoFurtherUploadIsMade()
    {
        Assert.NotNull(state.DuplicateResult);

        UploadCountingHandler counter = RequireUploadCounter();
        Assert.True(
            counter.UploadCount == 1,
            $"The Files API saw {counter.UploadCount} uploads across two submissions of the same bytes; a "
                + "duplicate's run stops at the registry, so ingest must not even ask the profile.");
    }

    [Then("the duplicate result carries no document handle")]
    public void ThenTheDuplicateResultCarriesNoDocumentHandle()
    {
        DocumentIngestResult duplicate = state.DuplicateResult
            ?? throw new InvalidOperationException("The When step produced no duplicate result.");

        Assert.False(duplicate.IsNewRegistration);
        Assert.Null(duplicate.TextLayer);
        Assert.Null(ContentHandleOf(duplicate));
    }

    [Then("the outbound message request carries the document block referencing the same {string}")]
    public void ThenTheOutboundRequestCarriesTheDocumentBlock(string expectedProperty)
    {
        Assert.Equal("file_id", expectedProperty);

        JsonElement body = RequestBody();
        JsonElement block = FindDocumentBlock(body);

        Assert.Equal("file", block.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(
            CannedFilesApiHandler.CannedFileId,
            block.GetProperty("source").GetProperty(expectedProperty).GetString());

        // The same id ingest recorded on the handle: the point of the handle is that the agent reads
        // the file ingest uploaded, not one prepared again behind its back.
        DocumentContentHandle handle = ContentHandleOf(RequireFirstResult())!;
        Assert.Equal(handle.ProviderToken, block.GetProperty("source").GetProperty(expectedProperty).GetString());
    }

    [Then("the outbound message request carries the {string} beta")]
    public void ThenTheOutboundRequestCarriesTheBeta(string expectedBeta)
    {
        CapturingMessagesHandler messages = RequireMessagesApi();

        // What this pins is that we SEND the opt-in. That the API would refuse the 1h TTL without it
        // is derived from the pinned SDK's model, not from a live call - the @requiresApi lane is
        // where the real answer gets settled. Sending it is the safe direction either way.
        Assert.True(
            messages.BetaHeader is not null,
            "The message request carried no anthropic-beta header, so the 1h cache TTL the document block "
                + "asks for would be sent without its declared opt-in.");
        Assert.Contains(expectedBeta, messages.BetaHeader!, StringComparison.Ordinal);

        // The agent conversation goes to the endpoint configuration pinned, not to whatever an
        // ambient variable named - the same regression the upload path carries, on the path that
        // sends the extracted document to the model.
        Uri endpoint = state.ResolvedEndpoint
            ?? throw new InvalidOperationException("The Given step did not record the resolved endpoint.");

        Assert.NotNull(messages.RequestUri);
        Assert.Equal(new Uri(endpoint, "/v1/messages?beta=true").AbsoluteUri, messages.RequestUri!.AbsoluteUri);
    }

    [Then("the live API accepts the request and answers")]
    public void ThenTheLiveApiAcceptsTheRequest()
    {
        CapturingMessagesHandler messages = RequireMessagesApi();

        Assert.True(messages.RequestCount > 0, "No message request reached the live API.");

        // The claim under test. A refusal here is the interesting outcome either way: if the API
        // rejects the request, the beta pairing this story sends is wrong and the SDK-derived claim
        // needs correcting rather than restating.
        Assert.True(
            state.AgentRunFailure is null,
            "The live API refused a document-carrying request that sent both beta opt-ins: "
                + $"{state.AgentRunFailure?.Message}");

        Assert.NotNull(messages.BetaHeader);
        Assert.Contains("extended-cache-ttl-2025-04-11", messages.BetaHeader!, StringComparison.Ordinal);
    }

    /// <summary>Finds the document block in an outbound message body, failing with the story's language.</summary>
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
            "The outbound message request carries no document block. MAF's Anthropic integration drops an "
                + "AIContent whose only payload is RawRepresentation, so an agent wired without the attachment "
                + "seam answers confidently about a document it was never shown.");
        throw new InvalidOperationException("Unreachable: Assert.Fail always throws.");
    }

    /// <summary>Wires a real ingest service whose document-content provider is the Claude profile.</summary>
    private void ArrangeIngestWithClaudeProfile()
    {
        string ingestRoot = Path.Combine(FindRepositoryRoot(), "data");
        string specimenPath = Path.Combine(ingestRoot, SpecimenFileName);

        Assert.True(File.Exists(specimenPath), $"The DE specimen is missing from the repository at '{specimenPath}'.");

        state.SpecimenPath = specimenPath;

        CannedFilesApiHandler canned = new();
        UploadCountingHandler counter = new(canned);

        state.CannedFilesApi = canned;
        state.UploadCounter = counter;
        state.UploadTransport = new HttpClient(counter);

        IDocumentContentProvider provider = BuildProvider(state.UploadTransport);

        DocumentIngestOptions options = new(ingestRoot, DocumentIngestOptions.ClaudeFilesApiLimitBytes);

        state.Service = ConstructIngestService(options, provider);

        Assert.Empty(state.Registry.Entries);
        Assert.Equal(0, counter.UploadCount);
    }

    /// <summary>
    /// Builds the ingest service through the constructor overload that accepts the port.
    /// </summary>
    /// <remarks>
    /// Bound by parameter name so the scenario survives the service gaining another optional
    /// collaborator, and fails loudly - as an assertion, not a build break - while the one this story
    /// adds does not exist.
    /// </remarks>
    private DocumentIngestService ConstructIngestService(
        DocumentIngestOptions options,
        IDocumentContentProvider provider)
    {
        ConstructorInfo constructor = typeof(DocumentIngestService).GetConstructors().Single();

        ParameterInfo[] parameters = constructor.GetParameters();

        Assert.True(
            Array.Exists(parameters, parameter => parameter.ParameterType == typeof(IDocumentContentProvider)),
            $"'{nameof(DocumentIngestService)}' takes no {nameof(IDocumentContentProvider)}, so ingest cannot "
                + "invoke the document-content profile and no run can carry a document handle.");

        object?[] arguments = new object?[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            arguments[index] = parameter switch
            {
                _ when parameter.ParameterType == typeof(IDocumentRegistry) => state.Registry,
                _ when parameter.ParameterType == typeof(IIngestAuditLog) => state.AuditLog,
                _ when parameter.ParameterType == typeof(DocumentIngestOptions) => options,
                _ when parameter.ParameterType == typeof(ITextLayerExtractor) =>
                    new PdfPigTextLayerExtractor(options, NullLogger<PdfPigTextLayerExtractor>.Instance),
                _ when parameter.ParameterType == typeof(ILogger<DocumentIngestService>) =>
                    NullLogger<DocumentIngestService>.Instance,
                _ when parameter.ParameterType == typeof(IDocumentContentProvider) => provider,
                _ when parameter.HasDefaultValue => parameter.DefaultValue,
                _ => throw new InvalidOperationException(
                    $"'{nameof(DocumentIngestService)}' requires an unrecognised parameter '{parameter.Name}'; "
                        + "extend this scenario."),
            };
        }

        return (DocumentIngestService)constructor.Invoke(arguments);
    }

    /// <summary>Reads the content handle off an ingest result by name, so its absence is an assertion.</summary>
    private static DocumentContentHandle? ContentHandleOf(DocumentIngestResult result)
    {
        PropertyInfo property = typeof(DocumentIngestResult)
                .GetProperty("ContentHandle", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"'{nameof(DocumentIngestResult)}' exposes no 'ContentHandle', so the file_id ingest paid for "
                    + "never reaches triage or the section agents.");

        return property.GetValue(result) as DocumentContentHandle;
    }

    /// <summary>Builds the Claude profile over the given transport, through the adapter's own entry point.</summary>
    /// <remarks>
    /// Every factory and every provider this creates is registered for disposal, not just the first.
    /// The seam scenario builds a second provider to redeem the checkpointed handle, and an earlier
    /// version of this method kept only the first - leaking a client and a run-scoped memo per
    /// scenario, which is exactly the lifetime mistake the port's own contract warns about.
    /// </remarks>
    private IDocumentContentProvider BuildProvider(HttpClient transport)
    {
        (object factory, Type factoryType) = BuildFactory(transport, state.IsLive);
        state.Track(factory as IDisposable);

        MethodInfo create = factoryType.GetMethod(
                "CreateDocumentContentProvider",
                BindingFlags.Public | BindingFlags.Instance,
                Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                $"'{factoryType.FullName}' exposes no 'CreateDocumentContentProvider()'.");

        IDocumentContentProvider provider = (IDocumentContentProvider?)create.Invoke(factory, [])
            ?? throw new InvalidOperationException("'CreateDocumentContentProvider' returned null.");

        state.Track(provider as IDisposable);

        return provider;
    }

    /// <summary>The environment variable a developer sets to opt into the live lane.</summary>
    private const string LiveKeyVariable = "ANTHROPIC_API_KEY";

    /// <summary>
    /// The live key as it stood before any scenario hook touched the environment.
    /// </summary>
    /// <remarks>
    /// Captured in <c>[BeforeTestRun]</c> for the reason the S01-05 bindings record: another class
    /// clears the ambient Anthropic variables in an untagged <c>[BeforeScenario]</c>, and hook order
    /// between classes is not guaranteed. The skip itself is that class's <c>@requiresApi</c> hook,
    /// which Reqnroll applies to every scenario carrying the tag - duplicating it here would add a
    /// second identical guard and no safety.
    /// </remarks>
    private static string? _liveApiKey;

    [BeforeTestRun]
    public static void CaptureLiveApiKeyForIngest() =>
        _liveApiKey = Environment.GetEnvironmentVariable(LiveKeyVariable);

    /// <summary>Constructs the adapter's factory over a transport, without naming an Anthropic type.</summary>
    private static (object Factory, Type FactoryType) BuildFactory(HttpClient transport, bool live = false)
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

    /// <summary>Binds <c>CreateDocumentAgent</c>'s arguments by name, so an added optional parameter is survivable.</summary>
    private static object?[] BuildDocumentAgentArguments(MethodInfo method, DocumentContent content)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object?[] arguments = new object?[parameters.Length];

        for (int index = 0; index < parameters.Length; index++)
        {
            ParameterInfo parameter = parameters[index];
            arguments[index] = parameter.Name switch
            {
                "name" => "docControl",
                "instructions" => "Extract, never interpret. Copy snippets verbatim.",
                "document" => content,
                "modelId" => HaikuSnapshot,
                _ when parameter.HasDefaultValue => parameter.DefaultValue,
                _ => throw new InvalidOperationException(
                    $"'CreateDocumentAgent' requires an unrecognised parameter '{parameter.Name}'; extend this scenario."),
            };
        }

        return arguments;
    }

    private static object? Read(object instance, string propertyName) =>
        instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance)
            ?? throw new InvalidOperationException(
                $"'{instance.GetType().FullName}' exposes no '{propertyName}'.");

    private async Task<DocumentReference> BuildReferenceAsync()
    {
        string specimenPath = RequireSpecimenPath();

        await using FileStream stream = File.OpenRead(specimenPath);
        ContentHash hash = await ContentHash.ComputeSha256Async(stream).ConfigureAwait(false);

        return new DocumentReference(hash.Canonical, new Uri(specimenPath), Path.GetFileName(specimenPath));
    }

    private async Task<DocumentIngestResult> IngestAsync()
    {
        DocumentIngestService service = state.Service
            ?? throw new InvalidOperationException("The Given step did not construct the ingest service.");

        return await service.IngestAsync(RequireSpecimenPath()).ConfigureAwait(false);
    }

    private JsonElement RequestBody()
    {
        CapturingMessagesHandler messages = RequireMessagesApi();

        Assert.True(messages.RequestCount > 0, "No message request reached the transport, so nothing was asserted on.");
        Assert.NotNull(messages.Body);

        return JsonDocument.Parse(messages.Body!).RootElement;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cbix.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"No directory containing 'Cbix.sln' was found above '{AppContext.BaseDirectory}'.");

        return directory!.FullName;
    }

    private string RequireSpecimenPath() =>
        state.SpecimenPath ?? throw new InvalidOperationException("The Given step did not resolve the specimen path.");

    private UploadCountingHandler RequireUploadCounter() =>
        state.UploadCounter ?? throw new InvalidOperationException("The Given step did not install a counting transport.");

    private HttpClient RequireUploadTransport() =>
        state.UploadTransport ?? throw new InvalidOperationException("The Given step did not build the upload transport.");

    private CapturingMessagesHandler RequireMessagesApi() =>
        state.MessagesApi ?? throw new InvalidOperationException("The When step did not capture a message request.");

    private DocumentIngestResult RequireFirstResult() =>
        state.FirstResult ?? throw new InvalidOperationException("No ingest result was recorded.");
}
