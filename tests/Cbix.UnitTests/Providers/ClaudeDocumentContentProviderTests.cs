using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Cbix.Core.Documents;
using Cbix.Providers.Anthropic;

namespace Cbix.UnitTests.Providers;

/// <summary>
/// Unit backstop for the Claude document-content profile (story S01-05), complementing the BDD
/// scenarios in <c>ClaudeDocumentContentProfile.feature</c>.
/// </summary>
/// <remarks>
/// <para>
/// The scenarios answer the story's question - uploaded once, referenced by <c>file_id</c>, marked
/// <c>cache_control</c>. These answer the questions the story does not ask out loud but the port
/// does: what a resumed handle does, what each class of provider failure means for the retry
/// decision, and whether a failed preparation can be repeated. Every one of them is asserted at the
/// transport, because the profile's claims are claims about HTTP traffic.
/// </para>
/// <para>
/// The profile is reached through <c>AnthropicAgentFactory.CreateDocumentContentProvider()</c>, the
/// same entry point production composition uses, over the internal transport seam. Nothing between
/// the port and the socket is stubbed: the real SDK builds the real multipart upload, and only the
/// far end is canned.
/// </para>
/// </remarks>
[Collection(AnthropicEnvironmentCollection.Name)]
public sealed class ClaudeDocumentContentProviderTests : IDisposable
{
    private const string ExplicitKey = "profile-test-explicit-key-not-a-real-credential";

    /// <summary>A minimal but genuine PDF header, so the bytes on the wire are recognisably a document.</summary>
    private const string DocumentBytes = "%PDF-1.7\nspecimen body\n%%EOF\n";

    private readonly string _root = Directory.CreateTempSubdirectory("cbix-claude-profile-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a suite over.
        }
    }

    [Fact]
    public async Task Prepare_UploadsOnceAndReturnsACacheableFileDocumentBlock()
    {
        using Harness harness = Harness.Create(this);
        DocumentReference document = Document("de-specimen.pdf");

        DocumentContent content = await harness.Provider.PrepareAsync(document);

        Assert.Equal(1, harness.Api.UploadCount);

        // The block that will be sent to the model, as JSON - which is what it becomes on the wire,
        // and the only way to assert on a provider payload without importing the type.
        JsonElement block = Block(content);
        Assert.Equal("document", block.GetProperty("type").GetString());
        Assert.Equal("file", block.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal(FilesApiHandler.DefaultFileId, block.GetProperty("source").GetProperty("file_id").GetString());

        JsonElement cacheControl = block.GetProperty("cache_control");
        Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());

        // The one-hour lifetime, not the five-minute default: a five-minute entry can expire between
        // one section agent and the next. See Compose's remarks for what the marker does and does not
        // buy - and for the beta opt-in the attaching call site owes.
        Assert.Equal("1h", cacheControl.GetProperty("ttl").GetString());

        // Native PDF mode presents the pages visually, so the Matrix agent can read the table
        // layout: this profile is the one that is NOT degraded.
        Assert.Equal("claude-native-pdf", content.Capabilities.ProfileName);
        Assert.True(content.Capabilities.IncludesVisualContent);
        Assert.False(content.Capabilities.IsDegraded);

        Assert.Equal(document.DocumentId, content.Handle.DocumentId);
        Assert.Equal(content.Capabilities.ProfileName, content.Handle.ProfileName);
        Assert.Equal(FilesApiHandler.DefaultFileId, content.Handle.ProviderToken);
    }

    [Fact]
    public async Task Prepare_SendsTheDocumentAsAMultipartUploadToTheFilesApi()
    {
        using Harness harness = Harness.Create(this);

        await harness.Provider.PrepareAsync(Document("de-specimen.pdf"));

        // The FULL absolute URI, checked against the endpoint the factory resolved from
        // configuration. A path-suffix assertion would pass while the document and the API key went
        // to an attacker's host, which is the exact failure the adapter's endpoint pinning exists to
        // prevent - and the upload path carries the same payload as the message path.
        Assert.Equal(
            new Uri(harness.ResolvedEndpoint, "/v1/files?beta=true").AbsoluteUri,
            harness.Api.LastUploadUri!.AbsoluteUri);
        Assert.Contains("multipart/form-data", harness.Api.LastUploadContentType!, StringComparison.Ordinal);

        // The upload deadline reaches the client rather than merely being assigned to an options
        // object: the SDK advertises its own timeout to the service in this header, so its presence
        // is the evidence that a stalled socket cannot hold the gate for the life of the run.
        Assert.Equal("300", harness.Api.LastUploadTimeoutHeader);

        // The beta header is what makes the Files API answer at all. The SDK adds it; this is the
        // assertion that notices when a package upgrade stops.
        Assert.Contains("files-api", harness.Api.LastUploadBetaHeader!, StringComparison.Ordinal);

        // The credential travels as X-Api-Key and never as a bearer token - the adapter's hardening
        // applies to the upload path too, not only to the message path.
        Assert.Equal(ExplicitKey, harness.Api.LastUploadApiKey);
        Assert.Null(harness.Api.LastUploadAuthorization);

        Assert.Contains("de-specimen.pdf", harness.Api.LastUploadBody!, StringComparison.Ordinal);
        Assert.Contains("%PDF-1.7", harness.Api.LastUploadBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_UploadsToTheConfiguredEndpointNotTheAmbientOne()
    {
        // The upload path carries the same two secrets as the message path - the API key and the
        // document itself - so it needs the same regression AnthropicWireTests keeps on /v1/messages.
        // With no explicit endpoint the SDK was measured sending requests to whatever
        // ANTHROPIC_BASE_URL names, with nothing in the logs to show it.
        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_BASE_URL", "https://attacker.example.invalid");
        scope.Set("ANTHROPIC_API_KEY", "an-ambient-developer-key");

        using Harness harness = Harness.Create(this);

        await harness.Provider.PrepareAsync(Document("de-specimen.pdf"));

        Assert.Equal("https://api.anthropic.com/v1/files?beta=true", harness.Api.LastUploadUri!.AbsoluteUri);
        Assert.Equal(ExplicitKey, harness.Api.LastUploadApiKey);
        Assert.Null(harness.Api.LastUploadAuthorization);
    }

    [Fact]
    public async Task Prepare_UploadsToAnExplicitlyConfiguredProxyEndpoint()
    {
        // Pinning the endpoint must not mean hardcoding it: an approved egress proxy (design 8) is a
        // configuration change, and it has to beat the ambient variable on this path too.
        const string Proxy = "https://egress-proxy.internal.example";

        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_BASE_URL", "https://attacker.example.invalid");

        using Harness harness = Harness.Create(this, configureOptions: options => options.BaseUrl = Proxy);

        await harness.Provider.PrepareAsync(Document("de-specimen.pdf"));

        Assert.Equal($"{Proxy}/v1/files?beta=true", harness.Api.LastUploadUri!.AbsoluteUri);
        Assert.Equal(ExplicitKey, harness.Api.LastUploadApiKey);
    }

    [Fact]
    public async Task Prepare_SendsNoAuthorizationHeaderWhenAnAmbientAuthTokenAppearsAfterConstruction()
    {
        // Validate() refuses to construct while ANTHROPIC_AUTH_TOKEN is set, so the variable is
        // introduced AFTER construction here - which is also the realistic residual case, since a
        // long-lived worker outlives changes to its environment. Without the adapter's explicit
        // `AuthToken = null`, the SDK sends Authorization: Bearer alongside X-Api-Key and the API
        // rejects the pair: a 401 mid-run, after checkpoints, on the upload nobody suspects.
        using Harness harness = Harness.Create(this);

        using EnvironmentVariableScope scope = new();
        scope.Set("ANTHROPIC_AUTH_TOKEN", "an-ambient-oauth-token");

        await harness.Provider.PrepareAsync(Document("de-specimen.pdf"));

        Assert.Null(harness.Api.LastUploadAuthorization);
        Assert.Equal(ExplicitKey, harness.Api.LastUploadApiKey);
        Assert.Equal("https://api.anthropic.com/v1/files?beta=true", harness.Api.LastUploadUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Prepare_MemoisesByDocumentIdRatherThanByReferenceIdentity()
    {
        // The memo key is the content-hash identity, so a re-registered path or a renamed file must
        // not pay for a second upload. A test that passed the same reference object twice would also
        // pass against an implementation keyed on reference identity.
        using Harness harness = Harness.Create(this);

        DocumentContent first = await harness.Provider.PrepareAsync(Document("de-specimen.pdf"));
        DocumentContent second = await harness.Provider.PrepareAsync(
            Document("de-specimen-renamed.pdf", documentId: Document("de-specimen.pdf").DocumentId));

        Assert.Equal(1, harness.Api.UploadCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Prepare_UploadsEachDistinctDocumentOnce()
    {
        // Guards the guard above: a memo that answered every document with the first one's content
        // would satisfy "uploaded once" while poisoning provenance for every later document.
        using Harness harness = Harness.Create(this);

        DocumentContent first = await harness.Provider.PrepareAsync(Document("de-specimen.pdf"));
        DocumentContent second = await harness.Provider.PrepareAsync(Document("ch-specimen.pdf", body: "%PDF-1.7\nch\n"));

        Assert.Equal(2, harness.Api.UploadCount);
        Assert.NotEqual(first.Handle.DocumentId, second.Handle.DocumentId);
    }

    [Fact]
    public async Task Prepare_ServesConcurrentCallersFromASingleUpload()
    {
        // The seven-way section fan-out is the real caller. Without the gate this is where a second
        // upload appears - intermittently, which is the worst way for it to appear.
        using Harness harness = Harness.Create(this);
        DocumentReference document = Document("de-specimen.pdf");

        DocumentContent[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => harness.Provider.PrepareAsync(document)));

        Assert.Equal(1, harness.Api.UploadCount);
        Assert.All(results, result => Assert.Same(results[0], result));
    }

    [Fact]
    public async Task Prepare_RedeemsAHandleWithoutUploadingAgain()
    {
        // The resume path: after a crash or a review pause the workflow hands back the checkpointed
        // handle, and the whole point is that the upload is not repeated.
        using Harness harness = Harness.Create(this);
        DocumentReference document = Document("de-specimen.pdf");
        DocumentContentHandle handle = new(document.DocumentId, "claude-native-pdf", "file_from_an_earlier_run");

        DocumentContent content = await harness.Provider.PrepareAsync(document, handle);

        Assert.Equal(0, harness.Api.UploadCount);
        Assert.Equal("file_from_an_earlier_run", content.Handle.ProviderToken);
        Assert.Equal(
            "file_from_an_earlier_run",
            Block(content).GetProperty("source").GetProperty("file_id").GetString());
    }

    [Fact]
    public async Task Prepare_ReuploadsWhenTheHandleCameFromAnotherProfile()
    {
        // Expected operational state after a provider swap, not an error: the token means nothing to
        // this profile, so it prepares again and issues a handle of its own rather than failing the
        // run over it.
        using Harness harness = Harness.Create(this);
        DocumentReference document = Document("de-specimen.pdf");
        DocumentContentHandle foreign = new(document.DocumentId, "generic-vision", "not-a-claude-file-id");

        DocumentContent content = await harness.Provider.PrepareAsync(document, foreign);

        Assert.Equal(1, harness.Api.UploadCount);
        Assert.Equal("claude-native-pdf", content.Handle.ProfileName);
        Assert.Equal(FilesApiHandler.DefaultFileId, content.Handle.ProviderToken);
    }

    [Fact]
    public async Task Prepare_ReuploadsWhenTheHandleCarriesNoToken()
    {
        // A handle issued by this profile but with nothing to resume from is not redeemable content;
        // treating it as one would compose a block with an empty file_id.
        using Harness harness = Harness.Create(this);
        DocumentReference document = Document("de-specimen.pdf");
        DocumentContentHandle tokenless = new(document.DocumentId, "claude-native-pdf");

        DocumentContent content = await harness.Provider.PrepareAsync(document, tokenless);

        Assert.Equal(1, harness.Api.UploadCount);
        Assert.Equal(FilesApiHandler.DefaultFileId, content.Handle.ProviderToken);
    }

    [Fact]
    public async Task Prepare_RefusesAHandleIssuedForADifferentDocument()
    {
        // The one mismatch that is never recoverable: redeeming it would prepare one document's
        // content under another's identity, which is cache poisoning and mis-attributed provenance
        // at once. The guard must fire before anything is uploaded or memoised.
        using Harness harness = Harness.Create(this);
        DocumentContentHandle other = new("sha256:some-other-document", "claude-native-pdf", "file_other");

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf"), other));

        Assert.Contains("sha256:some-other-document", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.Api.UploadCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Prepare_ReportsRetryableStatusesAsTransient(HttpStatusCode status)
    {
        using Harness harness = Harness.Create(this, api => api.Status = status);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf")));

        Assert.True(
            error.IsTransient,
            $"A {(int)status} must be retried with backoff, not queued for a human to look at.");
        Assert.Contains(((int)status).ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task Prepare_ReportsRequestAndCredentialFailuresAsPermanent(HttpStatusCode status)
    {
        // Retrying these burns the budget and delays the review queue by exactly the backoff: the
        // document, the request or the credential has to change first.
        using Harness harness = Harness.Create(this, api => api.Status = status);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf")));

        Assert.False(error.IsTransient);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task Prepare_ReportsAnUploadTimeoutAsTransientRatherThanAsCancellation()
    {
        // The measured hazard this defends: an elapsed client-side deadline escapes the SDK as a bare
        // TaskCanceledException, which the port defines as "the caller cancelled". A retry layer
        // reading it that way treats a stalled network as deliberate abandonment and drops the
        // document. The handler reproduces the symptom exactly - an OperationCanceledException on a
        // token that is not the caller's - without waiting out the real five-minute bound.
        using Harness harness = Harness.Create(this, api => api.TimeoutFailure = true);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf")));

        Assert.True(error.IsTransient, "An upload deadline is a condition of the moment, not a property of the document.");
        Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("transport", FailureKind(error));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "credential")]
    [InlineData(HttpStatusCode.Forbidden, "credential")]
    [InlineData(HttpStatusCode.BadRequest, "request")]
    [InlineData(HttpStatusCode.NotFound, "request")]
    [InlineData(HttpStatusCode.TooManyRequests, "provider")]
    [InlineData(HttpStatusCode.InternalServerError, "provider")]
    public async Task Prepare_RecordsWhatKindOfFailureItWas(HttpStatusCode status, string expectedKind)
    {
        // IsTransient alone answers "retry or review" and nothing else, so a revoked key arrives as N
        // indistinguishable per-document review entries with no signal that the fleet - not the
        // corpus - is broken. This is the second axis that lets an operator tell those apart.
        using Harness harness = Harness.Create(this, api => api.Status = status);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf")));

        Assert.Equal(expectedKind, FailureKind(error));
    }

    [Fact]
    public async Task Prepare_ReportsANetworkFailureAsTransient()
    {
        using Harness harness = Harness.Create(this, api => api.NetworkFailure = true);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf")));

        Assert.True(error.IsTransient);
        Assert.NotNull(error.InnerException);
        Assert.Equal("transport", FailureKind(error));
    }

    [Fact]
    public async Task Prepare_ReportsAnEmptyFileIdAsPermanent()
    {
        // A null id would otherwise be composed into a document block and surface much later as an
        // unintelligible model error, after the handle had been checkpointed.
        using Harness harness = Harness.Create(this, api => api.FileId = string.Empty);

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf")));

        Assert.False(error.IsTransient);
        Assert.Contains("file id", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_ReportsAMissingDocumentAsPermanentAndNeverCallsTheApi()
    {
        using Harness harness = Harness.Create(this);
        DocumentReference missing = new(
            "sha256:never-written",
            new Uri(Path.Combine(_root, "absent.pdf")),
            "absent.pdf");

        DocumentPreparationException error = await Assert.ThrowsAsync<DocumentPreparationException>(
            () => harness.Provider.PrepareAsync(missing));

        Assert.False(error.IsTransient);
        Assert.Equal("document", FailureKind(error));
        Assert.Equal(0, harness.Api.UploadCount);
    }

    [Fact]
    public async Task Prepare_DoesNotMemoiseAFailedPreparation()
    {
        // A transient failure that poisoned the memo would fail every remaining agent in the run for
        // the lifetime of the scope - turning one retryable moment into a lost document.
        using Harness harness = Harness.Create(this, api => api.Status = HttpStatusCode.ServiceUnavailable);
        DocumentReference document = Document("de-specimen.pdf");

        await Assert.ThrowsAsync<DocumentPreparationException>(() => harness.Provider.PrepareAsync(document));

        harness.Api.Status = HttpStatusCode.OK;
        DocumentContent content = await harness.Provider.PrepareAsync(document);

        Assert.Equal(2, harness.Api.UploadCount);
        Assert.Equal(FilesApiHandler.DefaultFileId, content.Handle.ProviderToken);
    }

    [Fact]
    public async Task Prepare_RejectsANullDocument()
    {
        using Harness harness = Harness.Create(this);

        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Provider.PrepareAsync(null!));
    }

    [Fact]
    public async Task Prepare_ObservesAnAlreadyCancelledToken()
    {
        using Harness harness = Harness.Create(this);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf"), cancellationToken: cancellation.Token));

        Assert.Equal(0, harness.Api.UploadCount);
    }

    [Fact]
    public async Task Prepare_ReportsCancellationDuringUploadAsCancellationRatherThanFailure()
    {
        // The distinction matters to the caller: a cancelled run must not be handed a "transient
        // failure" that a retry policy will sit and back off on.
        using CancellationTokenSource cancellation = new();
        using Harness harness = Harness.Create(this, api => api.OnRequest = () => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf"), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task CreateDocumentContentProvider_GivesEachRunScopeItsOwnMemo()
    {
        // The port makes the run-scoped lifetime part of the contract. This is what would fail if
        // the memo were ever moved onto the singleton factory: two runs would share one upload, and
        // one run's document would be readable from another's scope.
        FilesApiHandler api = new();
        using HttpClient transport = new(api);
        using AnthropicAgentFactory factory = new(new AnthropicProviderOptions
        {
            ApiKey = ExplicitKey,
            Transport = transport,
        });

        DocumentReference document = Document("de-specimen.pdf");

        IDocumentContentProvider first = factory.CreateDocumentContentProvider();
        IDocumentContentProvider second = factory.CreateDocumentContentProvider();

        Assert.NotSame(first, second);

        await first.PrepareAsync(document);
        await second.PrepareAsync(document);

        Assert.Equal(2, api.UploadCount);

        (first as IDisposable)?.Dispose();
        (second as IDisposable)?.Dispose();
    }

    [Fact]
    public void CreateDocumentContentProvider_RefusesAfterTheFactoryIsDisposed()
    {
        FilesApiHandler api = new();
        using HttpClient transport = new(api);
        AnthropicAgentFactory factory = new(new AnthropicProviderOptions
        {
            ApiKey = ExplicitKey,
            Transport = transport,
        });

        factory.Dispose();

        Assert.Throws<ObjectDisposedException>(factory.CreateDocumentContentProvider);
    }

    [Fact]
    public async Task Prepare_RefusesAfterTheProfileIsDisposed()
    {
        Harness harness = Harness.Create(this);
        harness.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => harness.Provider.PrepareAsync(Document("de-specimen.pdf")));
    }

    /// <summary>Reads the failure discriminator the profile records alongside the transience flag.</summary>
    /// <remarks>
    /// Read through the adapter's own constant rather than a literal: the key is a documented
    /// convention until the port grows a typed discriminator, and a test that spelled it out
    /// separately would keep passing after the convention moved.
    /// </remarks>
    private static string? FailureKind(DocumentPreparationException error) =>
        error.Data[ClaudeDocumentContentProvider.FailureKindKey] as string;

    /// <summary>Serialises the provider payload the block carries, which is what goes on the wire.</summary>
    private static JsonElement Block(DocumentContent content)
    {
        object payload = Assert.Single(content.Content).RawRepresentation
            ?? throw new InvalidOperationException("The prepared block carries no provider payload.");

        return JsonSerializer.SerializeToElement(payload, payload.GetType());
    }

    /// <summary>Writes a document to the test's temp root and returns a registry-shaped reference.</summary>
    private DocumentReference Document(string fileName, string? documentId = null, string body = DocumentBytes)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, body);

        // Derived from the bytes, as the registry derives it: two documents with the same content
        // must land on the same memo entry, and two with different content must not.
        string identity = documentId
            ?? $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}";

        return new DocumentReference(identity, new Uri(path), fileName);
    }

    /// <summary>A factory, its transport and the profile it produced, disposed together.</summary>
    private sealed class Harness : IDisposable
    {
        private Harness(FilesApiHandler api, HttpClient transport, AnthropicAgentFactory factory, IDocumentContentProvider provider)
        {
            Api = api;
            Transport = transport;
            Factory = factory;
            Provider = provider;
        }

        internal FilesApiHandler Api { get; }

        internal IDocumentContentProvider Provider { get; }

        /// <summary>The endpoint the factory resolved from configuration, for full-URI assertions.</summary>
        internal Uri ResolvedEndpoint => Factory.ResolvedEndpoint;

        private HttpClient Transport { get; }

        private AnthropicAgentFactory Factory { get; }

        internal static Harness Create(
            ClaudeDocumentContentProviderTests owner,
            Action<FilesApiHandler>? configure = null,
            Action<AnthropicProviderOptions>? configureOptions = null)
        {
            ArgumentNullException.ThrowIfNull(owner);

            FilesApiHandler api = new();
            configure?.Invoke(api);

            HttpClient transport = new(api);
            AnthropicProviderOptions options = new()
            {
                ApiKey = ExplicitKey,
                Transport = transport,
            };

            configureOptions?.Invoke(options);

            AnthropicAgentFactory factory = new(options);

            return new Harness(api, transport, factory, factory.CreateDocumentContentProvider());
        }

        public void Dispose()
        {
            (Provider as IDisposable)?.Dispose();
            Transport.Dispose();
            Factory.Dispose();
        }
    }

    /// <summary>
    /// Terminal handler standing in for the Claude Files API: it counts uploads, records what was
    /// sent, and answers with whatever the test configured.
    /// </summary>
    private sealed class FilesApiHandler : HttpMessageHandler
    {
        internal const string DefaultFileId = "file_unit_canned_specimen";

        private int _uploadCount;

        /// <summary>
        /// Uploads observed. Incremented atomically because the concurrency scenario drives eight
        /// callers at once, and a lost increment there would report a passing "uploaded once" for a
        /// provider that uploaded twice.
        /// </summary>
        internal int UploadCount => Volatile.Read(ref _uploadCount);

        internal Uri? LastUploadUri { get; private set; }

        internal string? LastUploadContentType { get; private set; }

        internal string? LastUploadBody { get; private set; }

        internal string? LastUploadApiKey { get; private set; }

        internal string? LastUploadAuthorization { get; private set; }

        internal string? LastUploadBetaHeader { get; private set; }

        /// <summary>The client-side deadline the SDK advertised, in whole seconds.</summary>
        internal string? LastUploadTimeoutHeader { get; private set; }

        /// <summary>The status to answer with. Settable mid-test, so a failure can be followed by a success.</summary>
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        /// <summary>The id the canned API issues. Empty models a provider that answered without one.</summary>
        internal string FileId { get; set; } = DefaultFileId;

        /// <summary>When set, the transport fails the way a dead socket does rather than answering.</summary>
        internal bool NetworkFailure { get; set; }

        /// <summary>
        /// When set, the transport fails the way an elapsed client-side deadline does: an
        /// <see cref="OperationCanceledException"/> on a token that is not the caller's.
        /// </summary>
        internal bool TimeoutFailure { get; set; }

        /// <summary>Runs when a request arrives, so a test can cancel mid-upload.</summary>
        internal Action? OnRequest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _uploadCount);
            LastUploadUri = request.RequestUri;
            LastUploadApiKey = Header(request, "X-Api-Key");
            LastUploadAuthorization = Header(request, "Authorization");
            LastUploadBetaHeader = Header(request, "anthropic-beta");
            LastUploadTimeoutHeader = Header(request, "X-Stainless-Timeout");

            if (request.Content is { } content)
            {
                LastUploadContentType = content.Headers.ContentType?.ToString();
                LastUploadBody = Encoding.Latin1.GetString(
                    await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            }

            OnRequest?.Invoke();

            if (NetworkFailure)
            {
                throw new HttpRequestException("The canned transport is simulating a dead socket.");
            }

            if (TimeoutFailure)
            {
                // Deliberately carries a token that is NOT the caller's, which is what makes this a
                // faithful stand-in for an elapsed ClientOptions.Timeout rather than for a caller
                // that gave up. Measured: the real deadline escapes as exactly this type.
                using CancellationTokenSource elapsed = new();
                await elapsed.CancelAsync().ConfigureAwait(false);

                throw new TaskCanceledException("The canned transport is simulating an elapsed upload deadline.", null, elapsed.Token);
            }

            string body = Status == HttpStatusCode.OK
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $$"""
                    {"id":"{{FileId}}","type":"file","filename":"specimen.pdf","mime_type":"application/pdf",
                     "size_bytes":{{LastUploadBody?.Length ?? 0}},"created_at":"2026-01-01T00:00:00Z","downloadable":false}
                    """)
                : """{"type":"error","error":{"type":"invalid_request_error","message":"canned failure"}}""";

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;
    }
}
