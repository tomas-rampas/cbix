using System.Globalization;
using System.Reflection;
using System.Text.Json;

using Cbix.Bdd.Support;
using Cbix.Core.Documents;
using Cbix.Core.Ingest;

using Reqnroll;

namespace Cbix.Bdd.Steps;

/// <summary>
/// Step bindings for <c>Features/ClaudeDocumentContentProfile.feature</c> (story S01-05).
/// </summary>
/// <remarks>
/// <para>
/// The adapter is reached by reflection and the port by its neutral interface, which is what makes
/// the red phase a failing assertion rather than a build break. Everything below the port is real:
/// a real <c>AnthropicClient</c> built by the real factory, the real SDK Files API call, the real
/// document block. Only the far end of the socket is canned, and in the <c>@requiresApi</c>
/// variants not even that.
/// </para>
/// <para>
/// <b>Why the API key is captured before the run rather than read in a step.</b> Another binding
/// class in this assembly clears the ambient Anthropic variables in an untagged
/// <c>[BeforeScenario]</c> hook, which Reqnroll runs for every scenario in the assembly, in no
/// guaranteed order relative to this one's. A live scenario that read the variable in a hook would
/// therefore skip or run depending on hook ordering. <c>[BeforeTestRun]</c> is ordered before every
/// scenario hook by construction, so capturing there is the only reading that is not a race.
/// </para>
/// </remarks>
[Binding]
public sealed class ClaudeDocumentContentProfileSteps(ClaudeDocumentContentProfileState state)
{
    /// <summary>The adapter assembly - the only place an Anthropic type may appear.</summary>
    private const string ProviderAssemblyName = "Cbix.Providers.Anthropic";

    /// <summary>The specimen named by the story's Gherkin, relative to the repository root.</summary>
    private const string SpecimenFileName = "Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf";

    /// <summary>
    /// A self-labelling fictional key for the canned scenarios. Construction and upload must both be
    /// offline, so a value that could never authenticate is sufficient - and carries no
    /// <c>sk-ant-</c> prefix, which secret scanners pattern-match.
    /// </summary>
    private const string DummyApiKey = "bdd-scenario-placeholder-key-not-a-real-credential";

    /// <summary>The environment variable a developer sets to opt into the live variants.</summary>
    private const string LiveKeyVariable = "ANTHROPIC_API_KEY";

    /// <summary>
    /// The live API key as it stood before any scenario hook touched the environment, or
    /// <see langword="null"/> when nobody supplied one.
    /// </summary>
    private static string? _liveApiKey;

    /// <summary>Captures the ambient live key before any scenario hook can clear it.</summary>
    [BeforeTestRun]
    public static void CaptureLiveApiKey() =>
        _liveApiKey = Environment.GetEnvironmentVariable(LiveKeyVariable);

    /// <summary>
    /// Skips the live variants - visibly - when no key was supplied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reqnroll's xUnit generator emits <c>[SkippableFact]</c>, so a <c>SkipException</c> raised
    /// here is reported as a skipped test with its reason attached. That matters more than it looks:
    /// the alternative - returning early - produces a scenario that reports itself as passed while
    /// having asserted nothing, which is the failure mode a release-gate suite exists to prevent.
    /// </para>
    /// <para>
    /// The <c>@requiresApi</c> tag is consumed by this hook and by nothing else. It is not yet wired
    /// to a CI filter; it is documentation with one mechanical consumer, and the day a pipeline
    /// wants to select or exclude these scenarios, the tag is already on them.
    /// </para>
    /// </remarks>
    [BeforeScenario("requiresApi")]
    public static void SkipUnlessLiveApiKeyIsPresent() =>
        Skip.If(
            string.IsNullOrWhiteSpace(_liveApiKey),
            $"Set {LiveKeyVariable} to run the live Files API variants; they upload the specimen to Anthropic.");

    /// <summary>Releases the transport and the factory the scenario built.</summary>
    [AfterScenario]
    public void DisposeScenarioResources() => state.Dispose();

    [Given("the DE specimen {string}")]
    public async Task GivenTheDeSpecimen(string relativePath)
    {
        await ArrangeAsync(relativePath, live: false);
    }

    [Given("the live Claude Files API and the DE specimen {string}")]
    public async Task GivenTheLiveClaudeFilesApiAndTheDeSpecimen(string relativePath)
    {
        await ArrangeAsync(relativePath, live: true);
    }

    [Given("the DE specimen was already uploaded in this run")]
    public async Task GivenTheDeSpecimenWasAlreadyUploaded()
    {
        await ArrangeAsync($"data/{SpecimenFileName}", live: false);
        await PrepareFirstAsync();
    }

    [Given("the live Claude Files API already uploaded the DE specimen in this run")]
    public async Task GivenTheLiveApiAlreadyUploadedTheDeSpecimen()
    {
        await ArrangeAsync($"data/{SpecimenFileName}", live: true);
        await PrepareFirstAsync();
    }

    [When("the Claude profile prepares document content for a first agent call")]
    public async Task WhenTheProfilePreparesContentForTheFirstAgentCall()
    {
        await PrepareFirstAsync();
    }

    [When("the Claude profile prepares document content for a second agent call")]
    public async Task WhenTheProfilePreparesContentForASecondAgentCall()
    {
        // The same reference object is deliberately NOT reused: the memo key is the document id, and
        // a scenario that passed the identical instance would also pass against an implementation
        // that keyed on reference identity - which would re-upload for every agent in the fan-out.
        state.SecondContent = await RequireProvider()
            .PrepareAsync(await BuildReferenceAsync(RequireSpecimenPath()))
            .ConfigureAwait(false);
    }

    [Then("the PDF is uploaded to the Claude Files API exactly once")]
    public void ThenThePdfIsUploadedExactlyOnce()
    {
        UploadCountingHandler counter = RequireCounter();

        Assert.True(
            counter.UploadCount == 1,
            $"The Files API saw {counter.UploadCount} uploads; the profile must upload each document once "
                + "and reuse it for the whole section-agent fan-out.");

        // The WHOLE absolute URI, against the endpoint the factory resolved from configuration. A
        // path-suffix check passes just as happily when the document and the API key have been sent
        // to an attacker-named host, which is the failure the adapter's endpoint pinning exists to
        // prevent - and the upload carries the same two secrets the message path does.
        Uri endpoint = state.ResolvedEndpoint
            ?? throw new InvalidOperationException("The Given step did not record the resolved endpoint.");

        Assert.NotNull(counter.LastUploadUri);
        Assert.Equal(new Uri(endpoint, "/v1/files?beta=true").AbsoluteUri, counter.LastUploadUri!.AbsoluteUri);

        // The Files API is a beta surface; without this header the request is rejected outright.
        Assert.NotNull(counter.LastUploadBetaHeader);
        Assert.Contains("files-api", counter.LastUploadBetaHeader!, StringComparison.Ordinal);

        if (state.CannedApi is not { } canned)
        {
            // Live variant: the body is on its way to Anthropic and was deliberately not read.
            return;
        }

        Assert.Equal(1, canned.UploadCount);
        Assert.NotNull(canned.LastUploadContentType);
        Assert.Contains("multipart/form-data", canned.LastUploadContentType!, StringComparison.Ordinal);

        // The document itself, not merely a request shaped like an upload. A profile that posted an
        // empty part would satisfy every assertion above.
        Assert.NotNull(canned.LastUploadBody);
        Assert.Contains("%PDF-", canned.LastUploadBody!, StringComparison.Ordinal);
        Assert.Contains(SpecimenFileName, canned.LastUploadBody!, StringComparison.Ordinal);
    }

    [Then("the returned content block references the resulting {string}")]
    public void ThenTheContentBlockReferencesTheFileId(string expectedProperty)
    {
        Assert.Equal("file_id", expectedProperty);

        DocumentContent content = RequireFirstContent();
        JsonElement block = DescribeDocumentBlock(content);

        Assert.Equal("document", block.GetProperty("type").GetString());

        JsonElement source = block.GetProperty("source");
        Assert.Equal("file", source.GetProperty("type").GetString());

        string? fileId = source.GetProperty(expectedProperty).GetString();
        Assert.False(string.IsNullOrWhiteSpace(fileId), "The document block carries no file id.");

        // The id in the block and the id in the checkpointed handle must be the same value: a resume
        // rebuilds the block from the handle, so a divergence would resume against a different file.
        Assert.Equal(fileId, content.Handle.ProviderToken);

        if (state.CannedApi is not null)
        {
            // Proves the id came from the API's response rather than from anything the profile
            // invented locally.
            Assert.Equal(CannedFilesApiHandler.CannedFileId, fileId);
        }
        else
        {
            Assert.StartsWith("file_", fileId!, StringComparison.Ordinal);
        }
    }

    [Then("{string} is set on the document block")]
    public void ThenCacheControlIsSetOnTheDocumentBlock(string expectedProperty)
    {
        Assert.Equal("cache_control", expectedProperty);

        JsonElement block = DescribeDocumentBlock(RequireFirstContent());

        Assert.True(
            block.TryGetProperty(expectedProperty, out JsonElement cacheControl),
            "The document block carries no cache_control, so every section agent would re-send the "
                + "document instead of hitting the prompt cache (design 5.1).");

        Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());

        // The one-hour lifetime rather than the five-minute default: a default entry can expire
        // between one section agent and the next. What the marker does and does not buy - and the
        // beta opt-in the attaching call site owes for this TTL - is on the profile's own remarks.
        Assert.Equal("1h", cacheControl.GetProperty("ttl").GetString());
    }

    [Then("no second upload call is made")]
    public void ThenNoSecondUploadCallIsMade()
    {
        // Asserted on the second preparation's result as well as the counter: a provider that threw
        // and left the count at one would otherwise read as a pass.
        Assert.NotNull(state.SecondContent);

        UploadCountingHandler counter = RequireCounter();
        Assert.True(
            counter.UploadCount == 1,
            $"The Files API saw {counter.UploadCount} uploads across two preparations of the same document; "
                + "the second must be served from the run-scoped memo.");
    }

    [Then("the same {string} is reused")]
    public void ThenTheSameFileIdIsReused(string expectedProperty)
    {
        Assert.Equal("file_id", expectedProperty);

        DocumentContent first = RequireFirstContent();
        DocumentContent second = state.SecondContent
            ?? throw new InvalidOperationException("The When step produced no second preparation.");

        string? firstId = DescribeDocumentBlock(first).GetProperty("source").GetProperty(expectedProperty).GetString();
        string? secondId = DescribeDocumentBlock(second).GetProperty("source").GetProperty(expectedProperty).GetString();

        Assert.False(string.IsNullOrWhiteSpace(secondId), "The second preparation produced no file id.");
        Assert.Equal(firstId, secondId);
        Assert.Equal(first.Handle, second.Handle);
    }

    /// <summary>
    /// Serialises the provider payload riding in the block's raw representation.
    /// </summary>
    /// <remarks>
    /// The block is an Anthropic SDK type and this assembly must not name one, so it is inspected as
    /// JSON. That is not a workaround: JSON is what the block becomes on the wire, so asserting on it
    /// is a stronger statement than asserting on a property of an object that might serialise
    /// differently.
    /// </remarks>
    private static JsonElement DescribeDocumentBlock(DocumentContent content)
    {
        Assert.NotEmpty(content.Content);

        object payload = content.Content[0].RawRepresentation
            ?? throw new InvalidOperationException(
                "The prepared content block carries no raw representation, so no provider payload would "
                    + "reach the model.");

        return JsonSerializer.SerializeToElement(payload, payload.GetType());
    }

    /// <summary>Builds the profile over either the canned Files API or the real one.</summary>
    private async Task ArrangeAsync(string relativePath, bool live)
    {
        string specimenPath = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(specimenPath), $"The DE specimen is missing from the repository at '{specimenPath}'.");

        state.SpecimenPath = specimenPath;
        state.Document = await BuildReferenceAsync(specimenPath).ConfigureAwait(false);

        HttpMessageHandler terminal;
        if (live)
        {
            terminal = new HttpClientHandler();
        }
        else
        {
            CannedFilesApiHandler canned = new();
            state.CannedApi = canned;
            terminal = canned;
        }

        UploadCountingHandler counter = new(terminal);
        state.Counter = counter;
        state.Transport = new HttpClient(counter);

        Assembly provider = LoadOrFail(ProviderAssemblyName);
        Type optionsType = FindExportedType(provider, "AnthropicProviderOptions");
        Type factoryType = FindExportedType(provider, "AnthropicAgentFactory");

        object options = Activator.CreateInstance(optionsType)
            ?? throw new InvalidOperationException($"'{optionsType.FullName}' could not be constructed.");

        SetProperty(optionsType, options, "ApiKey", live ? _liveApiKey : DummyApiKey);

        // Internal by design - a transport is not a configuration knob - so the scenario reaches it
        // the same way the unit suite does, without widening the adapter's surface to do it.
        SetProperty(optionsType, options, "Transport", state.Transport, nonPublic: true);

        object factory = Activator.CreateInstance(factoryType, options)
            ?? throw new InvalidOperationException($"'{factoryType.FullName}' could not be constructed.");

        state.Factory = factory as IDisposable;

        state.ResolvedEndpoint = factoryType
            .GetProperty("ResolvedEndpoint", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(factory) as Uri
            ?? throw new InvalidOperationException(
                $"'{factoryType.FullName}' exposes no 'ResolvedEndpoint'; the upload assertion cannot check "
                    + "where the document was actually sent.");

        MethodInfo create = factoryType.GetMethod(
                "CreateDocumentContentProvider",
                BindingFlags.Public | BindingFlags.Instance,
                Type.EmptyTypes)
            ?? throw new InvalidOperationException(
                $"'{factoryType.FullName}' exposes no parameterless 'CreateDocumentContentProvider()'; the "
                    + "Claude document-content profile has no composition entry point.");

        Assert.True(
            typeof(IDocumentContentProvider).IsAssignableFrom(create.ReturnType),
            $"'CreateDocumentContentProvider' returns '{create.ReturnType.FullName}', which is not the neutral "
                + $"'{nameof(IDocumentContentProvider)}' port every caller must see.");

        state.Provider = (IDocumentContentProvider?)create.Invoke(factory, [])
            ?? throw new InvalidOperationException("'CreateDocumentContentProvider' returned null.");
    }

    /// <summary>Prepares the document for the first time, recording the result.</summary>
    private async Task PrepareFirstAsync()
    {
        state.FirstContent = await RequireProvider()
            .PrepareAsync(RequireDocument())
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a registry-shaped reference: the identity is the content hash of the bytes on disk,
    /// exactly as the document registry derives it.
    /// </summary>
    private static async Task<DocumentReference> BuildReferenceAsync(string specimenPath)
    {
        await using FileStream stream = File.OpenRead(specimenPath);
        ContentHash hash = await ContentHash.ComputeSha256Async(stream).ConfigureAwait(false);

        return new DocumentReference(hash.Canonical, new Uri(specimenPath), Path.GetFileName(specimenPath));
    }

    /// <summary>Assigns a property by name, failing with the story's language rather than a null reference.</summary>
    private static void SetProperty(Type type, object instance, string propertyName, object? value, bool nonPublic = false)
    {
        BindingFlags flags = BindingFlags.Instance | (nonPublic ? BindingFlags.NonPublic : BindingFlags.Public);

        PropertyInfo property = type.GetProperty(propertyName, flags)
            ?? throw new InvalidOperationException(
                $"'{type.FullName}' exposes no settable '{propertyName}'.");

        property.SetValue(instance, value);
    }

    private static Assembly LoadOrFail(string assemblyName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            Assert.Fail($"Assembly '{assemblyName}' could not be loaded: {error.Message}");
            throw; // Unreachable: Assert.Fail always throws.
        }
    }

    private static Type FindExportedType(Assembly assembly, string typeName)
    {
        Type[] candidates = Array.FindAll(
            assembly.GetExportedTypes(),
            type => string.Equals(type.Name, typeName, StringComparison.Ordinal));

        Assert.True(
            candidates.Length > 0,
            $"No public type named '{typeName}' exists in '{assembly.GetName().Name}'.");
        Assert.True(
            candidates.Length == 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"'{assembly.GetName().Name}' declares {candidates.Length} public types named '{typeName}'."));

        return candidates[0];
    }

    /// <summary>Walks up from the test assembly's location to the directory holding <c>Cbix.sln</c>.</summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cbix.sln")))
        {
            directory = directory.Parent;
        }

        Assert.True(
            directory is not null,
            $"No directory containing 'Cbix.sln' was found above '{AppContext.BaseDirectory}'.");

        return directory!.FullName;
    }

    private IDocumentContentProvider RequireProvider() =>
        state.Provider ?? throw new InvalidOperationException("The Given step did not build the profile.");

    private DocumentReference RequireDocument() =>
        state.Document ?? throw new InvalidOperationException("The Given step did not build a document reference.");

    private string RequireSpecimenPath() =>
        state.SpecimenPath ?? throw new InvalidOperationException("The Given step did not resolve the specimen path.");

    private UploadCountingHandler RequireCounter() =>
        state.Counter ?? throw new InvalidOperationException("The Given step did not install a counting transport.");

    private DocumentContent RequireFirstContent() =>
        state.FirstContent ?? throw new InvalidOperationException("No preparation result was recorded.");
}
