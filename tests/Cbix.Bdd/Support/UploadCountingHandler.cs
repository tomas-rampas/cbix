namespace Cbix.Bdd.Support;

/// <summary>
/// Counts Files API upload requests passing through it and forwards every request unchanged.
/// </summary>
/// <remarks>
/// <para>
/// "Uploaded exactly once" is a claim about HTTP traffic, not about a counter the profile keeps
/// about itself, so it is measured here - at the transport - and the same handler serves both the
/// canned and the live arrangement. That is why this is a delegating handler rather than a stub:
/// the live scenarios need the request to actually reach Anthropic after being counted.
/// </para>
/// <para>
/// <b>It deliberately never reads the request body.</b> Reading a multipart upload consumes and
/// disposes the underlying file stream (measured on the pinned SDK), so a counting handler that
/// inspected the payload would break the live upload it is meant to observe. Body inspection
/// belongs to the terminal <see cref="CannedFilesApiHandler"/>, which has nothing left to forward
/// to.
/// </para>
/// </remarks>
public sealed class UploadCountingHandler : DelegatingHandler
{
    /// <summary>The Files API path, as the SDK spells it.</summary>
    private const string UploadPath = "/v1/files";

    /// <summary>Initialises a new <see cref="UploadCountingHandler"/>.</summary>
    /// <param name="innerHandler">The handler that actually answers - canned or the real network stack.</param>
    /// <exception cref="ArgumentNullException"><paramref name="innerHandler"/> is <see langword="null"/>.</exception>
    public UploadCountingHandler(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(innerHandler);
    }

    private int _uploadCount;

    private int _requestCount;

    /// <summary>Gets the number of Files API uploads observed on the wire.</summary>
    /// <remarks>
    /// Counted atomically. The profile is required to be safe under the concurrent section-agent
    /// fan-out, so the counter that judges it must not be the thing that loses an increment - a lost
    /// one would report a passing "uploaded once" for a provider that uploaded twice.
    /// </remarks>
    public int UploadCount => Volatile.Read(ref _uploadCount);

    /// <summary>Gets the number of requests of any kind observed on the wire.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Gets the URI of the most recent upload, or <see langword="null"/> if there was none.</summary>
    public Uri? LastUploadUri { get; private set; }

    /// <summary>Gets the <c>anthropic-beta</c> header sent with the most recent upload.</summary>
    /// <remarks>
    /// The Files API is a beta surface: without this header the request is rejected. The SDK adds it
    /// for us, and recording it here is what would notice if a package upgrade stopped doing so.
    /// </remarks>
    public string? LastUploadBetaHeader { get; private set; }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Interlocked.Increment(ref _requestCount);

        if (IsUpload(request))
        {
            Interlocked.Increment(ref _uploadCount);
            LastUploadUri = request.RequestUri;
            LastUploadBetaHeader = request.Headers.TryGetValues("anthropic-beta", out IEnumerable<string>? values)
                ? string.Join(",", values)
                : null;
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>Reports whether a request is a Files API upload rather than some other call.</summary>
    private static bool IsUpload(HttpRequestMessage request) =>
        request.Method == HttpMethod.Post
        && request.RequestUri is { } uri
        && uri.AbsolutePath.EndsWith(UploadPath, StringComparison.Ordinal);
}
