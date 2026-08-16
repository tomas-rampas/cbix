using System.Globalization;
using System.Net;
using System.Text;

namespace Cbix.Bdd.Support;

/// <summary>
/// Terminal handler that answers the Claude Files API locally with a canned upload response, so the
/// wire scenarios exercise the whole adapter without a key, a network or a bill.
/// </summary>
/// <remarks>
/// <para>
/// It answers <em>only</em> the upload endpoint. Anything else gets a 404, so a profile that started
/// making a second call - a metadata probe, a retry against a different route - fails the scenario
/// instead of passing quietly.
/// </para>
/// <para>
/// The multipart body is read here because this handler forwards nothing: reading consumes the
/// upload stream, which is exactly why <see cref="UploadCountingHandler"/> must not do it. Reading
/// it is also what lets a scenario assert the document actually went up, rather than just that
/// something did.
/// </para>
/// </remarks>
public sealed class CannedFilesApiHandler : HttpMessageHandler
{
    /// <summary>
    /// The file id the canned API issues. Self-describing rather than realistic: a value that could
    /// be mistaken for a real id in a log is a value someone will eventually try to fetch.
    /// </summary>
    public const string CannedFileId = "file_bdd_canned_de_specimen";

    private const string UploadPath = "/v1/files";

    /// <summary>Gets the number of upload requests this handler answered.</summary>
    public int UploadCount { get; private set; }

    /// <summary>Gets the <c>Content-Type</c> of the most recent upload body.</summary>
    public string? LastUploadContentType { get; private set; }

    /// <summary>Gets the length in bytes of the most recent upload body.</summary>
    public long LastUploadBodyLength { get; private set; }

    /// <summary>Gets the most recent upload body, decoded as Latin-1 so binary bytes survive the round trip.</summary>
    /// <remarks>
    /// Latin-1 maps every byte to exactly one character, so a PDF's binary content can be searched
    /// for a marker without a UTF-8 decoder replacing anything it dislikes.
    /// </remarks>
    public string? LastUploadBody { get; private set; }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Method != HttpMethod.Post
            || request.RequestUri is not { } uri
            || !uri.AbsolutePath.EndsWith(UploadPath, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"type":"error","error":{"type":"not_found_error","message":"The canned Files API answers uploads only."}}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        UploadCount++;

        if (request.Content is { } content)
        {
            LastUploadContentType = content.Headers.ContentType?.ToString();

            byte[] body = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            LastUploadBodyLength = body.Length;
            LastUploadBody = Encoding.Latin1.GetString(body);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildResponse(), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>Builds a Files API upload response in the shape the SDK deserialises.</summary>
    private string BuildResponse() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {"id":"{{CannedFileId}}","type":"file","filename":"Cross_Border_Trading_Legal_Instruction_DE_SPECIMEN.pdf",
             "mime_type":"application/pdf","size_bytes":{{LastUploadBodyLength}},"created_at":"2026-01-01T00:00:00Z",
             "downloadable":false}
            """);
}
