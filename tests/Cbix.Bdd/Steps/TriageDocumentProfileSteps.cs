using System.Globalization;
using System.Reflection;
using System.Text.Json;

using Cbix.Agnosticism;
using Cbix.Bdd.Support;
using Cbix.Core.Documents;
using Cbix.Core.Hosting;
using Cbix.Core.Ingest;
using Cbix.Core.Workflows;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/TriageDocumentProfile.feature</c> (story S01-14).
/// </summary>
/// <remarks>
/// <para>
/// <b>The graph is the production one in every lane.</b> Core's <c>AddCbixWorkflow</c> composes it,
/// the real ingest gate reads a committed specimen off disk, and MAF executes the real edges. The
/// canned lanes replace the model with an <c>IChatClient</c> that answers from a string; the wire
/// lane replaces the network under the real adapter; the live lane replaces nothing at all.
/// </para>
/// <para>
/// <b>The story's new surface is reached by reflection, on purpose.</b> <c>DocumentProfile</c>, the
/// profile on <see cref="TriagedDocument"/> and the document-bound agent seam do not exist when the
/// scenarios are first written, and the BDD working agreement is that a red phase must be a failing
/// assertion naming the missing behaviour rather than a build break that stops the whole test
/// project and names nothing. The reflection stays afterwards for the same reason it does in the
/// S01-12 bindings: it is what keeps the failure legible if the surface is renamed.
/// </para>
/// </remarks>
[Binding]
public sealed class TriageDocumentProfileSteps(TriageDocumentProfileState state)
{
    private const string ProviderAssemblyName = "Cbix.Providers.Anthropic";

    private const string DummyApiKey = "triage-scenario-placeholder-key-not-a-real-credential";

    /// <summary>The environment variable a developer sets to opt into the live lane.</summary>
    private const string LiveKeyVariable = "ANTHROPIC_API_KEY";

    /// <summary>The fields design Appendix A's <c>DocumentProfile</c> declares, in its order.</summary>
    private static readonly string[] DocumentProfileFields =
        ["DocType", "JurisdictionIso", "DocRef", "Version", "LayoutFamily", "Confidence"];

    /// <summary>
    /// The live key as it stood before any scenario hook touched the environment.
    /// </summary>
    /// <remarks>
    /// Captured in <c>[BeforeTestRun]</c> for the reason the S01-05 bindings record: another class in
    /// this assembly clears the ambient Anthropic variables in an untagged <c>[BeforeScenario]</c>,
    /// and hook order between classes is not guaranteed. The visible skip itself is that class's
    /// <c>@requiresApi</c> hook, which Reqnroll applies to every scenario carrying the tag.
    /// </remarks>
    private static string? _liveApiKey;

    [BeforeTestRun]
    public static void CaptureLiveApiKeyForTriage() =>
        _liveApiKey = Environment.GetEnvironmentVariable(LiveKeyVariable);

    [AfterScenario]
    public void DisposeScenarioResources() => state.Dispose();

    [Given("the DE specimen's document handle")]
    public void GivenTheDeSpecimensDocumentHandle() =>
        ComposeCanned(RepositoryLayout.DeSpecimenPath(), CannedProfileJson("DE", "CBTI-DE-2024", "4.2"));

    [Given("the CH specimen's document handle")]
    public void GivenTheChSpecimensDocumentHandle() =>
        ComposeCanned(RepositoryLayout.ChSpecimenPath(), CannedProfileJson("CH", "CBTI-CH-2024", "3.1"));

    [Given("a triage agent whose reply to the DE specimen is prose rather than the contract")]
    public void GivenAProseReply() =>
        ComposeCanned(RepositoryLayout.DeSpecimenPath(), "I think this is a German trading manual.");

    [Given("a triage agent whose reply to the DE specimen carries an undeclared field")]
    public void GivenAReplyWithAnUndeclaredField() =>
        ComposeCanned(
            RepositoryLayout.DeSpecimenPath(),
            AlterCannedProfile(profile => profile["Notes"] = "looks German"));

    [Given("a triage agent whose reply to the DE specimen omits a declared field")]
    public void GivenAReplyOmittingADeclaredField() =>
        ComposeCanned(
            RepositoryLayout.DeSpecimenPath(),
            AlterCannedProfile(profile => profile.Remove("LayoutFamily")));

    [Given("a triage agent whose reply to the DE specimen reports a confidence above one")]
    public void GivenAReplyWithAnOutOfRangeConfidence() =>
        ComposeCanned(
            RepositoryLayout.DeSpecimenPath(),
            AlterCannedProfile(profile => profile["Confidence"] = 1.4d));

    [Given("the DE specimen prepared for a Claude-backed triage run")]
    public void GivenTheDeSpecimenPreparedForAClaudeBackedTriageRun() =>
        ComposeClaudeBacked(RepositoryLayout.DeSpecimenPath(), live: false);

    [Given("the DE specimen prepared for a live Claude-backed triage run")]
    public void GivenTheDeSpecimenPreparedForALiveClaudeBackedTriageRun() =>
        ComposeClaudeBacked(RepositoryLayout.DeSpecimenPath(), live: true);

    [Given("the CH specimen prepared for a live Claude-backed triage run")]
    public void GivenTheChSpecimenPreparedForALiveClaudeBackedTriageRun() =>
        ComposeClaudeBacked(RepositoryLayout.ChSpecimenPath(), live: true);

    [When("the triage agent runs")]
    public async Task WhenTheTriageAgentRuns()
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

    [Then("it returns a DocumentProfile with JurisdictionIso {string}")]
    public void ThenItReturnsAProfileWithJurisdiction(string expected)
    {
        object profile = RequireProfile();

        Assert.Equal(expected, ReadString(profile, "JurisdictionIso"));

        // The whole contract, not only the field the step names. A profile that carried the right
        // jurisdiction with an empty doc reference would satisfy the story's wording and be useless
        // to every consumer of it - and design Appendix A declares all six as values, not options.
        foreach (string field in DocumentProfileFields)
        {
            Assert.True(
                profile.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance) is not null,
                $"'DocumentProfile' declares no '{field}'; design Appendix A's record shape is "
                    + $"({string.Join(", ", DocumentProfileFields)}).");
        }
    }

    [Then("DocType identifies it as a cross-border trading legal instruction")]
    public void ThenDocTypeIdentifiesTheDocument()
    {
        string docType = ReadString(RequireProfile(), "DocType");

        // Compared on a squashed form rather than literally: "Cross-Border", "Cross Border" and
        // "cross-border" are the same answer, and a live model chooses among them freely. What the
        // step is actually asserting is that triage recognised the document class at all, which is
        // what the routing in S01-15 keys off.
        string squashed = new string([.. docType.Where(char.IsLetterOrDigit)]).ToUpperInvariant();

        Assert.True(
            squashed.Contains("CROSSBORDER", StringComparison.Ordinal)
            && squashed.Contains("INSTRUCTION", StringComparison.Ordinal),
            $"Triage reported DocType '{docType}', which does not identify a cross-border trading legal "
                + "instruction.");
    }

    [Then("Confidence is reported as a value between 0 and 1")]
    public void ThenConfidenceIsInTheUnitInterval()
    {
        double confidence = ReadConfidence(RequireProfile());

        Assert.InRange(confidence, 0d, 1d);
    }

    [Then("no DocumentProfile is produced")]
    public void ThenNoProfileIsProduced()
    {
        TriagedDocument triaged = Triaged();

        Assert.True(
            ProfileProperty().GetValue(triaged) is null,
            "Triage produced a DocumentProfile from a reply that does not match the contract. A parser "
                + "that salvages a malformed reply is the model guessing with the pipeline's authority - "
                + "the one thing 'agents propose, code disposes' forbids.");
    }

    [Then("the refusal names what the reply broke")]
    public void ThenTheRefusalNamesWhatTheReplyBroke()
    {
        PropertyInfo failure = TriagedProperty(
            "ProfileParseFailure",
            "so a run cannot tell an unparseable reply from a missing one, and S01-15 has nothing to "
                + "route on");

        string? described = failure.GetValue(Triaged()) as string;

        Assert.False(
            string.IsNullOrWhiteSpace(described),
            "The triaged document records no reason for the refusal. A refusal nobody can read is a "
                + "silent drop with extra steps: the review queue row S01-15 writes has to say why the "
                + "document is there.");

        // The verbatim reply is still carried, because design 8's provenance record has to be able to
        // answer "what did the model actually say" about a run that produced nothing.
        Assert.False(string.IsNullOrEmpty(Triaged().AgentResponse));
    }

    [Then("the outbound triage request carries the document block referencing the uploaded {string}")]
    public void ThenTheOutboundTriageRequestCarriesTheDocumentBlock(string expectedProperty)
    {
        Assert.Equal("file_id", expectedProperty);

        JsonElement block = FindDocumentBlock(RequestBody());

        Assert.Equal("file", block.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(
            CannedFilesApiHandler.CannedFileId,
            block.GetProperty("source").GetProperty(expectedProperty).GetString());

        // Exactly one upload for the run, and the id triage sent is the one ingest paid for. The
        // alternative - triage preparing the document again behind ingest's back - would be invisible
        // in the extraction and visible only on the bill.
        Assert.Equal(1, RequireFilesApi().UploadCount);
    }

    [Then("the outbound triage request carries the {string} beta")]
    public void ThenTheOutboundTriageRequestCarriesTheBeta(string expectedBeta)
    {
        CapturingMessagesHandler messages = RequireMessagesApi();

        Assert.True(
            messages.BetaHeader is not null,
            "The triage request carried no anthropic-beta header. Without files-api the document block "
                + "cannot reference an uploaded file at all, and without extended-cache-ttl the 1h "
                + "cache_control TTL the profile sets is sent with no declared opt-in.");
        Assert.Contains(expectedBeta, messages.BetaHeader!, StringComparison.Ordinal);

        Uri endpoint = state.ResolvedEndpoint
            ?? throw new InvalidOperationException("The Given step did not record the resolved endpoint.");

        Assert.NotNull(messages.RequestUri);
        Assert.Equal(new Uri(endpoint, "/v1/messages?beta=true").AbsoluteUri, messages.RequestUri!.AbsoluteUri);
    }

    /// <summary>Composes the production graph with a canned reply behind the triage agent.</summary>
    /// <remarks>
    /// The composition is <c>Cbix.Agnosticism</c>'s, shared with stories S01-09 and S01-13 rather than
    /// copied: all three need the production <c>AddCbixWorkflow</c> with a stub chat client in the
    /// triage slot, and a second copy would let the agnosticism gate guard a topology this feature no
    /// longer describes.
    /// </remarks>
    private void ComposeCanned(string specimenPath, string cannedReply)
    {
        ServiceCollection services = [];
        services.AddLogging();
        services.AddStubBackedCbixWorkflow(RepositoryLayout.DataDirectory(), new StubChatClient(cannedReply));

        Build(services, specimenPath);
    }

    /// <summary>
    /// Composes the production graph with the real Anthropic adapter behind the triage agent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything below the seam is real: the real factory, the real Files API upload, the real
    /// document block, the real request the integration builds. On the offline lane only the network
    /// is canned, and the router is what lets one adapter client serve both endpoints the run touches.
    /// </para>
    /// <para>
    /// Adapter types are reached by reflection rather than named, matching the S01-08 and S01-12
    /// bindings: a compile-time reference here would make a missing adapter a build break instead of
    /// the failing assertion BDD requires, and it would put a provider assembly in this file's own
    /// reference table.
    /// </para>
    /// </remarks>
    private void ComposeClaudeBacked(string specimenPath, bool live)
    {
        state.IsLive = live;

        CannedFilesApiHandler files = new();
        // Two replies from story S01-16 onwards: the graph calls triage and then the DocControl agent,
        // and each parses its own reply strictly against its own contract. This feature asserts on the
        // FIRST request - triage's - but the run has to reach its end for the assertions about the
        // parsed profile to have anything to read, so the section agent needs a reply it accepts.
        CapturingMessagesHandler messages = live
            ? new CapturingMessagesHandler(new HttpClientHandler())
            : new CapturingMessagesHandler(
                CannedProfileJson(
                    specimenPath.Contains("_CH_", StringComparison.Ordinal) ? "CH" : "DE",
                    "CBTI-DE-2024",
                    "4.2"),
                CannedDeSpecimenReplies.DocControlSection());

        state.FilesApi = files;
        state.MessagesApi = messages;

        HttpMessageHandler transportHandler = live
            ? messages
            : new ClaudeEndpointRouter(files, messages);

        state.Track(transportHandler);

        HttpClient transport = new(transportHandler, disposeHandler: false);
        state.Track(transport);

        (object factory, Type factoryType) = BuildFactory(transport, live);
        state.Track(factory as IDisposable);

        state.ResolvedEndpoint = factoryType
            .GetProperty("ResolvedEndpoint", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(factory) as Uri
            ?? throw new InvalidOperationException(
                $"'{factoryType.FullName}' exposes no 'ResolvedEndpoint'; the scenario cannot check where "
                    + "the document was actually sent.");

        Type seam = DocumentBoundAgentFactoryType();

        MethodInfo create = factoryType.GetMethod("CreateDocumentAgentFactory", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"'{factoryType.FullName}' exposes no 'CreateDocumentAgentFactory'; there is no seam that "
                    + "gives the triage node an agent bound to the run's document, so the model would be "
                    + "asked to identify a document it was never shown.");

        ServiceCollection services = [];
        services.AddLogging();

        // Registered BEFORE Core's composition, which is how the host does it: Core's own
        // registrations are TryAdd-shaped, so whatever a host contributes first is what the graph uses.
        // Both model-calling nodes, because the run has to complete for this feature's assertions about
        // the parsed profile to have anything to read.
        foreach (string node in (string[])[CbixWorkflowNodes.Triage, CbixWorkflowNodes.SectionExtraction])
        {
            object agentFactory = create.Invoke(factory, BuildAgentFactoryArguments(create, node))
                ?? throw new InvalidOperationException("'CreateDocumentAgentFactory' returned null.");

            services.AddKeyedSingleton(seam, node, agentFactory);
        }

        services.AddSingleton<IDocumentContentProfileSource>(new ReflectedNativeDocumentSource(factory, factoryType));

        services.AddCbixWorkflow(
            new DocumentIngestOptions(RepositoryLayout.DataDirectory(), DocumentIngestOptions.ClaudeFilesApiLimitBytes),
            new DocumentPresentationOptions(DocumentPresentationCapability.NativeDocument));

        Build(services, specimenPath);
    }

    private void Build(ServiceCollection services, string specimenPath)
    {
        ServiceProvider container = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });

        state.Container = container;
        state.RunScope = container.CreateScope();
        state.SpecimenPath = specimenPath;
    }

    /// <summary>Constructs the adapter's factory over a transport, without naming an Anthropic type.</summary>
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

    /// <summary>Binds <c>CreateDocumentAgentFactory</c>'s arguments by name, so an added optional parameter survives.</summary>
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
                "instructions" => "Extract, never interpret. Copy values verbatim. Use only the supplied document.",
                _ when parameter.HasDefaultValue => parameter.DefaultValue,
                _ => throw new InvalidOperationException(
                    $"'CreateDocumentAgentFactory' requires an unrecognised parameter '{parameter.Name}'; "
                        + "extend this scenario."),
            };
        }

        return arguments;
    }

    /// <summary>The neutral document-bound agent seam, located by name so its absence is an assertion.</summary>
    private static Type DocumentBoundAgentFactoryType() =>
        typeof(CbixWorkflowFactory).Assembly.GetType("Cbix.Core.Agents.IDocumentBoundAgentFactory", throwOnError: false)
            ?? throw new InvalidOperationException(
                "'Cbix.Core.Agents.IDocumentBoundAgentFactory' does not exist, so a host has no neutral way "
                    + "to give a graph node an agent bound to the run's document - and the triage call would "
                    + "go out with no document attached.");

    /// <summary>A profile payload in exactly design Appendix A's shape.</summary>
    private static string CannedProfileJson(string jurisdiction, string docRef, string version) =>
        JsonSerializer.Serialize(BuildProfileFields(jurisdiction, docRef, version));

    private static Dictionary<string, object> BuildProfileFields(string jurisdiction, string docRef, string version) =>
        new(StringComparer.Ordinal)
        {
            ["DocType"] = "Cross-Border Trading Legal Instruction",
            ["JurisdictionIso"] = jurisdiction,
            ["DocRef"] = docRef,
            ["Version"] = version,
            ["LayoutFamily"] = "contoso-country-manual-v4",
            ["Confidence"] = 0.97d,
        };

    /// <summary>Builds a reply that is a well-formed profile in every respect but one.</summary>
    /// <remarks>
    /// Derived from the good payload rather than written out, so a refusal scenario cannot pass for
    /// the wrong reason: exactly one thing differs from the reply the happy path accepts.
    /// </remarks>
    private static string AlterCannedProfile(Action<Dictionary<string, object>> alteration)
    {
        Dictionary<string, object> fields = BuildProfileFields("DE", "CBTI-DE-2024", "4.2");
        alteration(fields);

        return JsonSerializer.Serialize(fields);
    }

    /// <summary>Finds the document block in an outbound message body, failing with the obligation's language.</summary>
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
            "The outbound triage request carries no document block. MAF's Anthropic integration drops an "
                + "AIContent whose only payload is RawRepresentation, so a triage node wired without the "
                + "attachment seam identifies a document it was never shown - and answers confidently.");
        throw new InvalidOperationException("Unreachable: Assert.Fail always throws.");
    }

    /// <summary>The parsed profile the run produced, or a failure naming what is missing.</summary>
    private object RequireProfile() =>
        ProfileProperty().GetValue(Triaged())
            ?? throw new InvalidOperationException(
                "Triage produced no DocumentProfile. The reply the agent gave was not parsed into design "
                    + "Appendix A's contract, so nothing downstream can route on jurisdiction, layout family "
                    + "or confidence.");

    private static PropertyInfo ProfileProperty() =>
        TriagedProperty(
            "Profile",
            "so the parsed DocumentProfile never reaches the graph and every consumer would have to "
                + "re-parse the agent's text");

    private static PropertyInfo TriagedProperty(string name, string consequence) =>
        typeof(TriagedDocument).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"'{nameof(TriagedDocument)}' exposes no '{name}', {consequence}.");

    private static string ReadString(object profile, string field)
    {
        PropertyInfo property = profile.GetType().GetProperty(field, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"'DocumentProfile' exposes no '{field}'; design Appendix A declares "
                    + $"({string.Join(", ", DocumentProfileFields)}).");

        string? value = property.GetValue(profile) as string;

        Assert.False(
            string.IsNullOrWhiteSpace(value),
            $"'DocumentProfile.{field}' is blank. Every field in the contract is a value, not an option: a "
                + "blank one is an answer nothing downstream can use and nothing here would otherwise catch.");

        return value!;
    }

    private static double ReadConfidence(object profile)
    {
        PropertyInfo property = profile.GetType().GetProperty("Confidence", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("'DocumentProfile' exposes no 'Confidence'.");

        return Convert.ToDouble(property.GetValue(profile), CultureInfo.InvariantCulture);
    }

    /// <summary>The message triage produced, read off the run's own event stream.</summary>
    private TriagedDocument Triaged()
    {
        ExecutorCompletedEvent completed = Assert.Single(
            state.Events.OfType<ExecutorCompletedEvent>(),
            candidate => string.Equals(candidate.ExecutorId, CbixWorkflowNodes.Triage, StringComparison.Ordinal));

        return Assert.IsType<TriagedDocument>(completed.Data);
    }

    private JsonElement RequestBody()
    {
        CapturingMessagesHandler messages = RequireMessagesApi();

        Assert.True(messages.RequestCount > 0, "No triage request reached the transport, so nothing was asserted on.");
        Assert.NotNull(messages.Body);

        return JsonDocument.Parse(messages.Body!).RootElement;
    }

    private IServiceScope RunScope =>
        state.RunScope ?? throw new InvalidOperationException(
            "No workflow was composed; a Given step must run before this one.");

    private CapturingMessagesHandler RequireMessagesApi() =>
        state.MessagesApi ?? throw new InvalidOperationException(
            "This lane records no Messages API traffic; only the wire and live lanes do.");

    private CannedFilesApiHandler RequireFilesApi() =>
        state.FilesApi ?? throw new InvalidOperationException(
            "This lane installs no canned Files API; only the wire lane does.");

    /// <summary>
    /// Contributes the adapter's native-document profile without naming an Anthropic type.
    /// </summary>
    /// <remarks>
    /// The production counterpart is <c>Cbix.Worker</c>'s <c>ClaudeDocumentContentProfileSource</c>,
    /// which is internal to the host. Re-declaring it here is not duplication of behaviour: the whole
    /// body is one reflective call onto the same adapter entry point the host calls.
    /// </remarks>
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
                ?? throw new InvalidOperationException(
                    $"'{factoryType.FullName}' exposes no 'CreateDocumentContentProvider()'.");

            return (IDocumentContentProvider?)create.Invoke(factory, [])
                ?? throw new InvalidOperationException("'CreateDocumentContentProvider' returned null.");
        }
    }
}
