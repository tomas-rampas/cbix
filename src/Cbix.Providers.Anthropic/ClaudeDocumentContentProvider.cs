using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using Cbix.Core.Documents;

using global::Anthropic;
using global::Anthropic.Core;
using global::Anthropic.Exceptions;
using global::Anthropic.Models.Beta.Files;
using global::Anthropic.Models.Beta.Messages;
using global::Anthropic.Services.Beta;

using Microsoft.Extensions.AI;

namespace Cbix.Providers.Anthropic;

/// <summary>
/// The Claude native-PDF profile of <see cref="IDocumentContentProvider"/>: uploads a document once
/// to the Files API and presents it as a document block referencing the resulting <c>file_id</c>,
/// marked <c>cache_control</c> so the whole section-agent fan-out reads it from the prompt cache
/// (design 5.1, story S01-05).
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything Claude-specific about document presentation lives here.</b> The Files API call, the
/// document block and its cache-control marker are named in this file and nowhere else; they leave
/// it only inside <see cref="AIContent.RawRepresentation"/>, which is the mechanism the port
/// documents for exactly this purpose. That is what lets the workflow, the executors and the seven
/// section agents talk about "the prepared document" without knowing a provider exists.
/// </para>
/// <para>
/// <b>Producing the block is not the same as sending it.</b> Measured on the pinned packages:
/// handing a <c>ChatMessage</c> an <see cref="AIContent"/> whose only payload is
/// <see cref="AIContent.RawRepresentation"/> produced an outbound <c>/v1/messages</c> body carrying
/// the text block and nothing else - the document block was dropped silently, with no error
/// anywhere. The carrier is still what the port mandates (see <see cref="DocumentContent"/>); what
/// it means is that <em>attaching</em> the prepared content is a separate obligation on the agent
/// call site, recorded against S01-12 and S01-16 in the sprint plan. Read that obligation before
/// wiring an agent to this profile.
/// </para>
/// <para>
/// <b>Lifetime: one instance per workflow-run scope</b>, as the port requires. The memo below is the
/// instance, so a singleton would cache uploads for the process's lifetime and leak one run's
/// document into another, while a transient would re-upload for every agent and defeat the point.
/// Instances are produced by <see cref="AnthropicAgentFactory.CreateDocumentContentProvider"/>,
/// which is a host-lifetime singleton: the client - and therefore the connection pool and the
/// credential - is shared across runs while the memo is not.
/// </para>
/// <para>
/// <b>The SDK's own retry is switched off for the upload, deliberately, and this is a measurement
/// rather than a preference.</b> On the pinned SDK 12.35.1 a retried multipart upload cannot
/// succeed: the request content is built once over the caller's stream, and sending it consumes and
/// disposes that stream, so attempt two fails with <c>ObjectDisposedException</c> ("Cannot access a
/// closed Stream") wrapped in <c>AnthropicIOException</c>. The damage is not the wasted attempts -
/// it is that a plain 429 arrives at this class as an opaque I/O error, having lost the status code
/// that decides <see cref="DocumentPreparationException.IsTransient"/>. Measured with a capturing
/// handler: with the SDK's default of two retries a 429 surfaced as <c>AnthropicIOException</c>;
/// with retries off it surfaced as <c>AnthropicRateLimitException</c> carrying
/// <c>StatusCode = TooManyRequests</c>. Retry belongs to the caller, which re-enters
/// <see cref="PrepareAsync"/> and opens the file again (design 9).
/// </para>
/// <para>
/// <b>A resumed <c>file_id</c> is never proved to be alive, and there is no automated recovery when
/// it is not.</b> A token can die between runs - retention expiry, deletion, a rotated key that
/// scopes files differently - and no offline check can tell. No metadata probe is made on the resume
/// path, deliberately: it would add a round trip to every resume to catch a rare case, and the
/// port's rule is to re-prepare rather than fail. A dead token therefore surfaces where the file is
/// actually used, as a 404 from the <em>agent's</em> message call rather than from this method.
/// </para>
/// <para>
/// <b>State the consequence plainly rather than implying the run scope absorbs it.</b> It does not:
/// a resumed run is by definition handed the checkpointed handle, so re-entering
/// <see cref="PrepareAsync"/> with that same stale token rebuilds the same dead block, and the loop
/// does not exit on its own. Until something can mark a token invalid - discarding it from the
/// checkpoint and from this memo so the next preparation uploads afresh - a downstream 404-on-use
/// makes that checkpoint effectively unresumable, and the run has to be started over. That
/// invalidation entry point is a real gap and is deliberately not built here, because it belongs
/// with the retry and resume loop in Sprint 02/03 that would call it; whoever owns that story owns
/// closing this. Nothing here deletes the remote artefact either - retention is design 11's open
/// question, not this profile's.
/// </para>
/// </remarks>
internal sealed class ClaudeDocumentContentProvider : IDocumentContentProvider, IDisposable
{
    /// <summary>
    /// The profile's stable name. It reaches the capability descriptor, the checkpointed handle and
    /// the eval harness's per-profile metrics, so it is a published identifier and should not change
    /// casually.
    /// </summary>
    internal const string ProfileName = "claude-native-pdf";

    /// <summary>
    /// Key under which every <see cref="DocumentPreparationException"/> raised here records what
    /// kind of failure it was, in <see cref="Exception.Data"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DocumentPreparationException"/> carries one axis - <c>IsTransient</c> - which
    /// answers "retry or review" and nothing else. That leaves the operationally important case
    /// invisible: a revoked or misscoped API key fails every document permanently, and without a
    /// second axis it arrives as N indistinguishable "this document needs a human" entries, with no
    /// signal that the fleet has a credential problem rather than a bad corpus.
    /// </para>
    /// <para>
    /// <b>This is the smallest honest mechanism available here, not the right one.</b> The right one
    /// is a typed discriminator on the exception in <c>Cbix.Core</c>, which is a change to the port's
    /// own contract and belongs with the story that builds the retry and alerting path around it.
    /// Until then this is a documented convention, discoverable only by a reader who knows to look -
    /// which is exactly why the limitation is written down rather than left implied.
    /// </para>
    /// </remarks>
    internal const string FailureKindKey = "cbix.document_preparation.failure_kind";

    /// <summary>The credential is refused or not entitled: every document will fail the same way until it changes.</summary>
    internal const string CredentialFailure = "credential";

    /// <summary>The request or the document was rejected on its own terms - this document, not the fleet.</summary>
    internal const string RequestFailure = "request";

    /// <summary>The document's bytes could not be read locally.</summary>
    internal const string DocumentFailure = "document";

    /// <summary>The API could not be reached, or did not answer in time.</summary>
    internal const string TransportFailure = "transport";

    /// <summary>The provider answered, but with a throttle, a server error, or something unusable.</summary>
    internal const string ProviderFailure = "provider";

    /// <summary>
    /// How long a single upload attempt may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An unbounded upload is the worst failure this class can have, because of the gate.</b> The
    /// SDK leaves <c>ClientOptions.Timeout</c> unset and its <see cref="HttpClient"/> has no
    /// deadline of its own, so a stalled socket does not merely hang one preparation - it hangs it
    /// while holding <see cref="_gate"/>, and every other agent in the fan-out queues behind it for
    /// the life of the run. A bound is therefore not defensive padding; it is what keeps one dead
    /// connection from costing a whole document.
    /// </para>
    /// <para>
    /// Five minutes is chosen against the input, not plucked: the ingest gate caps a document at the
    /// Files API's own limit, and an upload of that size needs minutes on a poor link but never
    /// tens of them. It is deliberately far longer than a healthy upload so that a slow network is
    /// tolerated rather than papered over by a retry storm.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Buffer for the upload stream. Large enough that a multi-megabyte PDF is not read in hundreds
    /// of syscalls, small enough to stay off the large object heap.
    /// </summary>
    private const int UploadBufferSize = 64 * 1024;

    /// <summary>
    /// Prepared documents, keyed by <see cref="DocumentReference.DocumentId"/> alone - the identity
    /// is a content hash, so two references sharing it are the same bytes, and keying on the
    /// location or the display name would pay for a second upload of a document already prepared.
    /// </summary>
    /// <remarks>
    /// Guarded by a monitor on itself rather than by <see cref="_gate"/>, so a hit never waits for
    /// an upload in progress: the gate serialises misses only. Failures are never stored - a run
    /// that could not reach the API must be able to try again, and a memoised failure would poison
    /// the whole run scope.
    /// </remarks>
    private readonly Dictionary<string, DocumentContent> _prepared = new(StringComparer.Ordinal);

    /// <summary>
    /// Serialises preparation misses so two agents racing on the same document upload it once.
    /// </summary>
    /// <remarks>
    /// One gate for the instance rather than one per document: the port scopes an instance to a
    /// single workflow run, and a run is one document (design 5), so per-document gates would be an
    /// unbounded dictionary keyed by something that never has a second entry. The cost of being
    /// wrong about that is a second document's preparation waiting for the first - slower, never
    /// incorrect.
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The Files API, with the SDK's broken multipart retry disabled. See the class remarks.</summary>
    private readonly IFileService _files;

    private int _disposed;

    /// <summary>Initialises a new <see cref="ClaudeDocumentContentProvider"/> over a shared client.</summary>
    /// <param name="client">
    /// The factory's Anthropic client. Shared, not owned: it carries the credential and the
    /// connection pool, and this class neither disposes it nor exposes it.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    internal ClaudeDocumentContentProvider(AnthropicClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        // Resolved once. WithOptions was measured to preserve the credential, the configured
        // endpoint and the injected transport, changing only what is assigned here - so this scopes
        // both settings to the upload path without touching the client every agent shares.
        _files = client.Beta.Files.WithOptions(options =>
        {
            options.MaxRetries = 0;
            options.Timeout = UploadTimeout;
            return options;
        });
    }

    /// <inheritdoc />
    public async Task<DocumentContent> PrepareAsync(
        DocumentReference document,
        DocumentContentHandle? resumeFrom = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        // Evaluated before the memo is consulted, because it is the one check that must never be
        // skipped: a handle belonging to another document is a programming error whose silent
        // outcomes are cache poisoning and mis-attributed provenance, and IsRedeemableBy throws for
        // it. A handle from another profile merely returns false - an expected state after a
        // provider swap - and costs one repeat upload.
        bool redeemable = resumeFrom?.IsRedeemableBy(document, ProfileName) == true;

        if (TryGetPrepared(document.DocumentId, out DocumentContent? memoised))
        {
            return memoised;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Re-checked under the gate: the racing caller that was ahead of us has finished by now,
            // and this is what turns "upload once" from a probability into a guarantee.
            if (TryGetPrepared(document.DocumentId, out memoised))
            {
                return memoised;
            }

            string fileId = redeemable && !string.IsNullOrWhiteSpace(resumeFrom!.ProviderToken)
                ? resumeFrom.ProviderToken!
                : await UploadAsync(document, cancellationToken).ConfigureAwait(false);

            DocumentContent prepared = Compose(document, fileId);

            lock (_prepared)
            {
                _prepared[document.DocumentId] = prepared;
            }

            return prepared;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes the profile to further preparations and drops the memo. The shared client belongs to
    /// the factory and is left alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type is disposable so a DI container can end the run scope deterministically - the memo's
    /// eviction bound is the run, and "it goes away when the scope does" should be a mechanism, not
    /// an expectation.
    /// </para>
    /// <para>
    /// <b>Clearing the memo is bookkeeping, not a barrier.</b> A preparation already in flight when
    /// this runs will finish and write its entry back into the dictionary, so the map is not
    /// guaranteed empty afterwards. That is accepted rather than defended against: the disposed flag
    /// is what refuses <em>new</em> work, the instance is being dropped by its scope in any case, and
    /// a locked-out write-back would turn an ordinary shutdown race into an exception in a
    /// <c>finally</c>.
    /// </para>
    /// <para>
    /// <b><see cref="_gate"/> is deliberately not disposed.</b> A <see cref="SemaphoreSlim"/> needs
    /// disposal only once its <c>AvailableWaitHandle</c> has been allocated, which this class never
    /// touches. Disposing it anyway would introduce the one race that disposal is supposed to
    /// prevent: a preparation in flight when the scope ends would fail in its <c>finally</c> with
    /// <see cref="ObjectDisposedException"/> from <c>Release</c>, masking whatever the run was
    /// actually doing.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_prepared)
        {
            _prepared.Clear();
        }
    }

    /// <summary>
    /// Builds the content, capabilities and handle for a document that is available under
    /// <paramref name="fileId"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The document block is the provider payload, and it rides in
    /// <see cref="AIContent.RawRepresentation"/> so no signature outside this assembly names it. It
    /// serialises to exactly what the Messages API expects (measured):
    /// <c>{"type":"document","source":{"type":"file","file_id":"..."},"cache_control":{"type":"ephemeral","ttl":"1h"}}</c>.
    /// </para>
    /// <para>
    /// <b>What <c>cache_control</c> actually buys, stated against this design's own topology rather
    /// than in general.</b> Three constraints bound it, and none of them is hypothetical here:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///   <b>A parallel fan-out cannot read a cache that does not exist yet.</b> An entry becomes
    ///   readable only once the response that wrote it has begun; seven section agents dispatched
    ///   simultaneously therefore all miss, and each pays the cache-<em>write</em> premium on the
    ///   document instead of the discount. Realising the saving needs the fan-out staggered - one
    ///   call primes the cache, the rest follow - which is a scheduling decision belonging to the
    ///   workflow topology (S01-13), not to this class. Marking the block is necessary for that
    ///   saving and nowhere near sufficient.
    ///   </description></item>
    ///   <item><description>
    ///   <b>Cache entries are scoped to a model.</b> The Matrix agent runs on Sonnet while the other
    ///   sections run on Haiku (design 7), so the Sonnet call can never read what a Haiku call
    ///   wrote. The tiering split is deliberate and worth more than the cache is; the consequence is
    ///   simply that the document is written into two caches, not one.
    ///   </description></item>
    ///   <item><description>
    ///   <b>The default lifetime is far shorter than this pipeline's pauses.</b> Ephemeral entries
    ///   default to five minutes, and a human-review pause (design 5.7) is measured in days, so a
    ///   resumed run is a certain miss no matter what. <c>Ttl1h</c> is taken here because the longer
    ///   entry costs a larger write premium once and covers the whole of a normal run including its
    ///   retries, where the five-minute entry can expire between one section agent and the next. It
    ///   does not pretend to survive a review pause; nothing available does.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>The one-hour TTL is a beta the request must opt into.</b> It is carried on the
    /// <em>message</em> call, not on this block, so the call site that attaches this content owns
    /// sending it - the same call site that owes the attachment itself (see the class remarks and
    /// the plan's S01-12 / S01-16 obligation). A request that carries the block without that opt-in
    /// is refused by the API rather than quietly downgraded.
    /// </para>
    /// </remarks>
    private static DocumentContent Compose(DocumentReference document, string fileId)
    {
        BetaRequestDocumentBlock block = new(new BetaFileDocumentSource(fileId))
        {
            // Marks the document as cacheable, with the one-hour TTL rather than the five-minute
            // default. What this does and does not buy is stated on Compose's remarks, because the
            // saving is conditional on things this class does not control - and a comment claiming
            // a saving that the topology cannot deliver is worse than no comment.
            CacheControl = new BetaCacheControlEphemeral { Ttl = Ttl.Ttl1h },
        };

        AIContent content = new() { RawRepresentation = block };

        return new DocumentContent(
            [content],
            new DocumentContentCapabilities(ProfileName, includesVisualContent: true, isDegraded: false),
            new DocumentContentHandle(document.DocumentId, ProfileName, fileId));
    }

    /// <summary>Reports whether an HTTP status is worth another attempt.</summary>
    /// <remarks>
    /// <para>
    /// Rate limiting, request timeout and every 5xx are conditions of the moment. Everything else in
    /// the 4xx range is a property of the request, the document or the credential: retrying a 401 or
    /// a 413 burns the retry budget and delays the review queue by exactly the backoff.
    /// </para>
    /// <para>
    /// <b>404 deserves a note, because it is ambiguous here and permanent either way.</b> Measured
    /// against the live API: <c>POST /v1/files</c> with a credential that is not entitled to the
    /// Files API answers <c>404 not_found_error</c>, not <c>401</c> - the route is masked rather than
    /// refused. So a 404 on this path may mean "no such file", "no such route" or "not your route",
    /// and no caller can tell them apart. That ambiguity is exactly why the resume policy re-prepares
    /// instead of diagnosing, and why this status is not retried.
    /// </para>
    /// </remarks>
    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
        || (int)status >= 500;

    /// <summary>Classifies a status so an operator can separate a fleet-wide credential problem from a bad document.</summary>
    /// <remarks>
    /// 401 and 403 are the ones that matter: they are properties of the credential, so they will fail
    /// every document in the estate identically until someone rotates a key. A 404 is deliberately
    /// <em>not</em> classified as a credential failure despite the measurement above - it is genuinely
    /// ambiguous, and a discriminator that guesses is worse than one that admits the request was
    /// refused on its own terms.
    /// </remarks>
    private static string ClassifyStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => CredentialFailure,
        HttpStatusCode.TooManyRequests => ProviderFailure,
        _ when (int)status >= 500 => ProviderFailure,
        _ => RequestFailure,
    };

    /// <summary>
    /// Builds the port's failure type, naming the document by identity rather than by path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The message reaches operational logs and the review queue, so it names the document and never
    /// the credential, the client options or the provider's response body - the underlying exception
    /// carries those for a debugger that has already been trusted with them.
    /// </para>
    /// <para>
    /// <b>Both identifiers are sanitised, and only one of them was already safe.</b>
    /// <see cref="DocumentReference.FileName"/> is character-validated by its own constructor -
    /// no control characters, no Unicode line separators - precisely because it reaches headers and
    /// logs. <see cref="DocumentReference.DocumentId"/> is <em>not</em>: it is checked for emptiness
    /// and length only, so a caller that constructed a reference by hand could put a newline in it
    /// and forge a log line through this message. In practice the registry derives it from a content
    /// hash, but "in practice" is not a control, so it is scrubbed here.
    /// </para>
    /// </remarks>
    private static DocumentPreparationException Fail(
        DocumentReference document,
        string reason,
        bool isTransient,
        string failureKind,
        Exception? innerException = null)
    {
        DocumentPreparationException failure = new(
            $"The Claude document-content profile could not prepare document '{ForMessage(document.DocumentId)}' "
                + $"('{document.FileName}'): {reason}",
            isTransient,
            innerException);

        failure.Data[FailureKindKey] = failureKind;

        return failure;
    }

    /// <summary>Renders an identifier safely for a log line or a review-queue entry.</summary>
    /// <remarks>
    /// Substitution rather than rejection: this runs on a path that is already reporting a failure,
    /// and throwing here would replace a diagnosable error with an undiagnosable one. The rules match
    /// what <see cref="DocumentReference"/> enforces on a file name - control characters, Unicode
    /// format characters and the line and paragraph separators that <c>char.IsControl</c> misses.
    /// </remarks>
    private static string ForMessage(string value)
    {
        Span<char> scrubbed = value.Length <= 256 ? stackalloc char[value.Length] : new char[256];
        int length = Math.Min(value.Length, scrubbed.Length);

        for (int index = 0; index < length; index++)
        {
            char character = value[index];

            scrubbed[index] = char.IsControl(character)
                || char.GetUnicodeCategory(character)
                    is UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator
                ? '�'
                : character;
        }

        return new string(scrubbed[..length]);
    }

    /// <summary>Opens the document's bytes for upload.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Uri.LocalPath"/> is opened directly, which the port's contract permits: a reference
    /// published by the registry has already been resolved (no symlink, junction or <c>..</c>
    /// segment), contained under the ingest root, and checked to round-trip to the path whose bytes
    /// were hashed. Re-validating here would duplicate a check this class cannot do correctly - it
    /// cannot see the ingest root.
    /// </para>
    /// <para>
    /// The transience split is the port's: an unreadable file is a property of the document and goes
    /// to review, while a generic <see cref="IOException"/> is most often a sharing violation on a
    /// file still being written into the watched share, which is precisely a "try again shortly".
    /// <see cref="PathTooLongException"/> is caught ahead of that despite deriving from
    /// <see cref="IOException"/> - a path does not get shorter on the second attempt, and letting it
    /// fall through would have spent the whole retry budget on a certainty.
    /// </para>
    /// <para>
    /// The argument-shaped failures are wrapped rather than allowed to escape. The port says a
    /// profile reports its failures as <see cref="DocumentPreparationException"/>; a raw
    /// <see cref="ArgumentException"/> from deep inside a file-system call would reach a caller whose
    /// catch block reads it as "the caller passed something wrong", which is a different bug in a
    /// different place.
    /// </para>
    /// </remarks>
    private static FileStream OpenDocument(DocumentReference document)
    {
        try
        {
            return new FileStream(
                document.Location.LocalPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = UploadBufferSize,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Fail(
                document,
                "the document is not at its registered location.",
                isTransient: false,
                DocumentFailure,
                error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw Fail(
                document,
                "the worker is not permitted to read the document.",
                isTransient: false,
                DocumentFailure,
                error);
        }
        catch (PathTooLongException error)
        {
            throw Fail(
                document,
                "the document's registered path is too long for the file system.",
                isTransient: false,
                DocumentFailure,
                error);
        }
        catch (IOException error)
        {
            throw Fail(
                document,
                "the document could not be opened; it may still be being written.",
                isTransient: true,
                DocumentFailure,
                error);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            throw Fail(
                document,
                "the document's registered location is not a path this host can open.",
                isTransient: false,
                DocumentFailure,
                error);
        }
    }

    /// <summary>Uploads the document to the Files API and returns the issued <c>file_id</c>.</summary>
    private async Task<string> UploadAsync(DocumentReference document, CancellationToken cancellationToken)
    {
        MediaTypeHeaderValue mediaType;
        try
        {
            mediaType = MediaTypeHeaderValue.Parse(document.MediaType);
        }
        catch (FormatException error)
        {
            throw Fail(
                document,
                $"'{ForMessage(document.MediaType)}' is not a usable media type.",
                isTransient: false,
                RequestFailure,
                error);
        }

        await using FileStream stream = OpenDocument(document);

        FileMetadata metadata;
        try
        {
            metadata = await _files.Upload(
                    new FileUploadParams
                    {
                        // Betas is deliberately left unset: the SDK's Files service already sends
                        // `anthropic-beta: files-api-2025-04-14`, and naming it here was measured to
                        // send the value twice.
                        File = new BinaryContent
                        {
                            Stream = stream,

                            // Safe to pass through: DocumentReference rejects quotes, separators and
                            // control characters in a file name precisely because it lands in a
                            // multipart Content-Disposition header here.
                            FileName = document.FileName,
                            ContentType = mediaType,
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        // This filter is first on purpose, and it is deliberately wider than a typed catch: the SDK
        // may deliver a cancellation as its own I/O exception, so the TOKEN decides, not the
        // exception type. The cost is that a 401 arriving in the same instant as a cancellation is
        // reported as a cancellation - which is the right answer anyway, because a caller that
        // cancelled has abandoned the run and has nothing to do with a status code it never waited
        // for.
        catch (Exception error) when (cancellationToken.IsCancellationRequested)
        {
            // The port says caller cancellation surfaces as OperationCanceledException, not as a
            // transient preparation failure a retry policy would sit and back off on.
            throw new OperationCanceledException(
                "Document preparation was cancelled while uploading to the Claude Files API.",
                error,
                cancellationToken);
        }
        catch (OperationCanceledException error)
        {
            // Reached only when the caller's token is NOT the reason - so this is the client-side
            // upload deadline expiring. Measured on the pinned SDK: an elapsed ClientOptions.Timeout
            // escapes as a bare TaskCanceledException, indistinguishable at the type level from
            // deliberate abandonment. Letting it through would tell a retry layer the caller gave up,
            // and the document would be dropped rather than retried - so it is translated here into
            // exactly what it is.
            throw Fail(
                document,
                "the Files API did not respond within the upload timeout.",
                isTransient: true,
                TransportFailure,
                error);
        }
        catch (AnthropicApiException error)
        {
            throw Fail(
                document,
                $"the Files API answered {(int)error.StatusCode}.",
                IsTransient(error.StatusCode),
                ClassifyStatus(error.StatusCode),
                error);
        }
        catch (AnthropicIOException error)
        {
            // Network-level: connection refused, TLS failure, socket reset.
            throw Fail(document, "the Files API could not be reached.", isTransient: true, TransportFailure, error);
        }
        catch (HttpRequestException error)
        {
            throw Fail(document, "the Files API could not be reached.", isTransient: true, TransportFailure, error);
        }
        catch (AnthropicException error)
        {
            // Everything the SDK raises that is neither a status nor a socket - a malformed
            // response, a validation refusal. Retrying reproduces it, so it goes to review.
            throw Fail(
                document,
                "the Anthropic client rejected the upload.",
                isTransient: false,
                ProviderFailure,
                error);
        }

        if (string.IsNullOrWhiteSpace(metadata.ID))
        {
            // Not defensive padding: a null id would otherwise be composed into a document block and
            // fail later as an unintelligible model error, having already been checkpointed.
            throw Fail(
                document,
                "the Files API returned no file id, so there is nothing for an agent to reference.",
                isTransient: false,
                ProviderFailure);
        }

        return metadata.ID;
    }

    /// <summary>Reads the memo under its own lock.</summary>
    private bool TryGetPrepared(string documentId, [NotNullWhen(true)] out DocumentContent? prepared)
    {
        lock (_prepared)
        {
            return _prepared.TryGetValue(documentId, out prepared);
        }
    }
}
