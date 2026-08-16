using System.Net.Mime;

namespace Cbix.Core.Documents;

/// <summary>
/// Identifies one source document to an <see cref="IDocumentContentProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type answers <em>which</em> document, never <em>how</em> to read it. Everything a
/// particular profile needs beyond identity - a PDF byte stream, the PDFPig text layer, a
/// page renderer - is a collaborator injected into that profile, so adding a profile never
/// widens this type. It carries BCL types only.
/// </para>
/// <para>
/// <b>Trust boundary.</b> A <see cref="DocumentReference"/> reaches file-system and network code,
/// so its construction is validated, not merely documented. The split of responsibility is:
/// <em>this type enforces scheme and shape</em> - an absolute <c>file://</c> URI, a bare display
/// file name, a well-formed media type; <em>the registry / ingest layer enforces containment</em> -
/// it resolves the path and verifies the resolved location lies under the configured ingest root
/// before constructing a reference. Neither check substitutes for the other: shape validation
/// cannot see the configured root, and a containment check on an unvalidated shape is checking the
/// wrong string.
/// </para>
/// <para>
/// <see cref="DocumentId"/> is the caching key: a provider that uploads or renders must do
/// so once per <see cref="DocumentId"/>, no matter how many times it is called.
/// </para>
/// <para>
/// Members are get-only on purpose. The compiler-generated copy constructor behind a <c>with</c>
/// expression bypasses this constructor's validation, so adding <c>init</c> setters would quietly
/// open a route around every check below.
/// </para>
/// </remarks>
public sealed record DocumentReference
{
    /// <summary>Windows device names that are still special when used as a file name, with or without an extension.</summary>
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Initialises a new <see cref="DocumentReference"/>.</summary>
    /// <param name="documentId">
    /// Stable identity of the document in the document registry.
    /// <para>
    /// <b>The registry must derive this from the bytes it actually read</b> - a content hash of the
    /// stored document - and must never pass through an identity supplied by whoever submitted the
    /// document. Two things break otherwise: memoisation turns into cache poisoning (a submitter
    /// who names their document with an existing id gets the other document's prepared content and
    /// its cached upload), and provenance is mis-attributed (published values point at the wrong
    /// source document, which is precisely what the audit bar in design 8 must not allow).
    /// </para>
    /// </param>
    /// <param name="location">
    /// Absolute <c>file://</c> location of the document bytes, already resolved and
    /// containment-checked against the ingest root by the registry.
    /// </param>
    /// <param name="fileName">
    /// Bare display name of the document, including extension - never a path segment. Providers
    /// that upload use it as the upload's display name only; it must not be concatenated into a
    /// path, and must not be written unencoded into an HTTP header or multipart part.
    /// </param>
    /// <param name="mediaType">IANA media type of the document bytes. Defaults to <c>application/pdf</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="location"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Any argument is empty or white space; <paramref name="location"/> is relative, is not a
    /// <c>file://</c> URI, or is a UNC path; <paramref name="fileName"/> is not a bare file name;
    /// or <paramref name="mediaType"/> is not a well-formed media type.
    /// </exception>
    public DocumentReference(string documentId, Uri location, string fileName, string mediaType = "application/pdf")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        ValidateLocation(location);
        ValidateFileName(fileName);
        ValidateMediaType(mediaType);

        DocumentId = documentId;
        Location = location;
        FileName = fileName;
        MediaType = mediaType;
    }

    /// <summary>
    /// Gets the stable registry identity of the document, derived by the registry from the bytes it
    /// read. It is the memoisation key for document preparation and the join key for provenance.
    /// </summary>
    public string DocumentId { get; }

    /// <summary>Gets the absolute <c>file://</c> location of the document bytes.</summary>
    public Uri Location { get; }

    /// <summary>Gets the bare display file name, including extension.</summary>
    public string FileName { get; }

    /// <summary>Gets the IANA media type of the document bytes.</summary>
    public string MediaType { get; }

    /// <summary>
    /// Fails closed on the location: absolute <c>file://</c> only.
    /// </summary>
    /// <remarks>
    /// Ingest reads from a watched file share today, so <c>file</c> is the only scheme any profile
    /// can currently service. Widen this deliberately - and with its own tests - when an
    /// object-store ingest profile actually exists; an allowlist that anticipates schemes nobody
    /// implements is an open door with no user. UNC is rejected separately from scheme: a
    /// <c>file://server/share</c> URI is a remote SMB fetch, which on Windows can be coerced into
    /// leaking NTLM credentials to an attacker-chosen host.
    /// </remarks>
    private static void ValidateLocation(Uri location)
    {
        if (!location.IsAbsoluteUri)
        {
            throw new ArgumentException("The document location must be an absolute URI.", nameof(location));
        }

        if (!string.Equals(location.Scheme, Uri.UriSchemeFile, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The document location must use the '{Uri.UriSchemeFile}' scheme; '{location.Scheme}' is not serviced by any ingest profile.",
                nameof(location));
        }

        if (location.IsUnc)
        {
            throw new ArgumentException(
                "The document location must be a local path: a UNC location is a remote SMB fetch and can leak credentials to the host that names it.",
                nameof(location));
        }
    }

    /// <summary>Fails closed on the file name: a bare display name, nothing that can act as a path or a header injection.</summary>
    private static void ValidateFileName(string fileName)
    {
        if (fileName.AsSpan().IndexOfAny('/', '\\') >= 0)
        {
            throw new ArgumentException(
                "The file name must be a bare name, not a path: directory separators are not allowed.",
                nameof(fileName));
        }

        if (fileName.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The file name must not contain ':': it introduces a Windows volume or alternate-data-stream reference.",
                nameof(fileName));
        }

        if (fileName.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The file name must not contain '\"': it is how a display name breaks out of a quoted header field.",
                nameof(fileName));
        }

        if (fileName is "." or ".." || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The file name must not contain '..': relative traversal is not a display name.",
                nameof(fileName));
        }

        foreach (char character in fileName)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    "The file name must not contain control characters: they enable header and log injection downstream.",
                    nameof(fileName));
            }
        }

        int extensionStart = fileName.IndexOf('.', StringComparison.Ordinal);
        string baseName = extensionStart < 0 ? fileName : fileName[..extensionStart];
        if (ReservedDeviceNames.Contains(baseName))
        {
            throw new ArgumentException(
                $"'{baseName}' is a reserved Windows device name and must not be used as a file name.",
                nameof(fileName));
        }
    }

    /// <summary>
    /// Validates the media type against the media-type grammar rather than against an allowlist of
    /// values: the pipeline may legitimately grow beyond PDF, but no legitimate media type contains
    /// a control character or a CRLF that would split a header downstream.
    /// </summary>
    private static void ValidateMediaType(string mediaType)
    {
        foreach (char character in mediaType)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException(
                    "The media type must not contain control characters.",
                    nameof(mediaType));
            }
        }

        try
        {
            ContentType parsed = new(mediaType);
            if (string.IsNullOrWhiteSpace(parsed.MediaType))
            {
                throw new ArgumentException("The media type is not well formed.", nameof(mediaType));
            }
        }
        catch (FormatException error)
        {
            throw new ArgumentException($"The media type '{mediaType}' is not well formed.", nameof(mediaType), error);
        }
    }
}
