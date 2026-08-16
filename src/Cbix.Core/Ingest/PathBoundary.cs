using System.Globalization;

namespace Cbix.Core.Ingest;

/// <summary>
/// Path predicates the ingest trust boundary is built from, shared by the options that validate the
/// configured root and the service that judges each submission.
/// </summary>
/// <remarks>
/// These live in one place because the two callers must agree exactly. A root accepted under one
/// rule and a submission judged under another is not a boundary - it is two boundaries with a gap
/// between them, and the gap is where the escape goes.
/// </remarks>
internal static class PathBoundary
{
    /// <summary>
    /// Reports whether a path begins with two slashes of either kind - the shape of a UNC share
    /// (<c>\\server\share</c>) or a device-namespace path (<c>\\?\</c>, <c>\\.\</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is checked before anything touches the file system.</b> Merely resolving a UNC
    /// path makes Windows authenticate outbound to whatever host the path names - measured at
    /// roughly eleven seconds of block, and an NTLM exchange offered to a machine the submitter
    /// chose. The I/O also fails as an <see cref="IOException"/>, which a caller reasonably reads as
    /// transient and retries, so the credential exposure repeats. The refusal therefore has to be
    /// decided on the string, before any syscall.
    /// </para>
    /// <para>
    /// The device prefixes matter for a second reason: <c>\\?\</c> disables the normalisation that
    /// <see cref="Path.GetFullPath(string)"/> relies on, so a device path can express a location
    /// that no amount of prefix comparison judges correctly.
    /// </para>
    /// <para>
    /// <b>The two characters are literals, deliberately - not <see cref="Path.DirectorySeparatorChar"/>
    /// and <see cref="Path.AltDirectorySeparatorChar"/>.</b> On Linux both of those are <c>/</c>, so a
    /// platform-aware predicate simply does not see a backslash: <c>\\attacker.example\share\x.pdf</c>
    /// stopped being a UNC path and became an ordinary relative filename, fell through to normal
    /// resolution, and the refusal never fired. That is not a portability nicety - it silently
    /// disabled a security control on the platform the service actually deploys to, and the tests
    /// that were meant to prove the control passed on Windows while failing on Linux CI.
    /// </para>
    /// <para>
    /// Neither shape is ever a legitimate document submission in this system on any operating system.
    /// On Windows they are SMB and device vectors; on Linux a filename beginning with two backslashes
    /// is pathological input that no ingest share produces. So the rule is uniform and fails closed
    /// everywhere - and being platform-independent is exactly what makes the refusal testable
    /// everywhere, with the same inputs producing the same reason on every host.
    /// </para>
    /// </remarks>
    public static bool HasUncOrDevicePrefix(string path) =>
        path.Length >= 2 && IsSlash(path[0]) && IsSlash(path[1]);

    /// <summary>
    /// Gets the string comparison that matches the host file system's own idea of path equality.
    /// </summary>
    /// <remarks>
    /// Comparing case-sensitively on Windows would reject a document submitted as <c>C:\Ingest\...</c>
    /// against a root recorded as <c>C:\ingest</c>, and comparing case-insensitively on Linux would
    /// accept a path the file system considers a different directory - a containment check that
    /// disagrees with the file system is not a containment check.
    /// <para>
    /// <b>Recorded assumption:</b> case sensitivity is treated as a property of the operating
    /// system, not of the individual directory. Windows and macOS volumes can be configured
    /// per-directory the other way (Windows 10+ per-directory case sensitivity, a case-sensitive
    /// APFS volume). Probing the real behaviour of every directory on every submission is not worth
    /// its cost or its failure modes here: the mismatch can only make the check stricter on a
    /// case-sensitive directory under Windows, never looser, because a resolved path that differs
    /// only in case still has to lie under the root's own spelling.
    /// </para>
    /// </remarks>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// Builds the prefix a contained path must start with: the root plus a trailing separator.
    /// </summary>
    /// <remarks>
    /// The trailing separator is load-bearing - a bare prefix test accepts
    /// <c>C:\ingest-evil\payload.pdf</c> as being inside <c>C:\ingest</c>. Appending it
    /// unconditionally is equally wrong: a drive root is already <c>C:\</c>, and
    /// <see cref="Path.TrimEndingDirectorySeparator(string)"/> deliberately refuses to trim a root,
    /// so the naive form yields <c>C:\\</c> and refuses every document on the drive.
    /// </remarks>
    public static string ContainmentPrefix(string root) =>
        Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

    /// <summary>Reports whether <paramref name="path"/> lies strictly under <paramref name="root"/>.</summary>
    public static bool IsUnder(string root, string path) =>
        path.StartsWith(ContainmentPrefix(root), PathComparison);

    /// <summary>Maximum length of the path prefix rendered into a log event.</summary>
    private const int MaxLoggedPathLength = 400;

    /// <summary>Suffix marking a path that was cut short, so a short rendering is never mistaken for a short path.</summary>
    private const string TruncationMarker = "...[truncated]";

    /// <summary>
    /// Renders a path safely for a log event: control, Unicode format and surrogate characters
    /// replaced, length bounded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Refusal events are the one place in ingest that logs strings somebody else chose, so they are
    /// the one place that has to assume those strings are hostile. A carriage return forges a second
    /// log entry; U+202E (RIGHT-TO-LEFT OVERRIDE) makes <c>annex&lt;RLO&gt;fdp.exe</c> render as
    /// <c>annexexe.pdf</c> to whoever reviews the alert - the same trick <c>DocumentReference</c>
    /// rejects in file names, arriving here instead. Refusing to log would be worse than logging
    /// carefully: the event is the control that monitors the ingest share's write restriction.
    /// </para>
    /// <para>
    /// <b>Surrogates are replaced wherever they appear, not merely where truncation could split
    /// them.</b> A lone surrogate is not a character: it renders as a replacement glyph at best, and
    /// at worst breaks the encoder in whichever sink receives it - turning a security event into a
    /// lost one. That hazard belongs to the character, not to the truncation boundary: a path
    /// containing an unpaired surrogate anywhere carries it, and .NET strings admit one happily
    /// because they are UTF-16 code units rather than text. Replacing on the category is therefore
    /// the whole fix, and it makes the old back-off-one-character-before-cutting-a-pair dance
    /// unnecessary - which is why that code is gone rather than kept alongside.
    /// </para>
    /// <para>
    /// The cost, stated: a <em>well-formed</em> surrogate pair - an emoji, a CJK extension character,
    /// anything outside the Basic Multilingual Plane - has both halves replaced, so a legitimate
    /// astral-plane file name renders as two replacement characters. That is accepted deliberately.
    /// The alternative is pair-aware scanning that admits exactly the code units this method exists
    /// to neutralise, in return for prettier rendering of a path that is, by construction, being
    /// logged because a submission was refused. A path full of replacement characters is a strong
    /// signal about that submission, which is the job.
    /// </para>
    /// <para>
    /// The value is replaced, never dropped: an operator needs to see that something was there, and
    /// a replacement character in an alert is itself a strong signal about the submission.
    /// </para>
    /// </remarks>
    public static string ForLog(string path)
    {
        if (path.Length <= MaxLoggedPathLength)
        {
            return string.Create(path.Length, path, static (destination, original) => Sanitise(destination, original));
        }

        // A truncated path that does not say it was truncated is a path an operator will read as
        // complete - and then compare, fruitlessly, against a real name on the share. The marker
        // costs one suffix and removes that whole class of wasted investigation. It is appended after
        // sanitisation of the kept prefix, so nothing in the input can forge it.
        return string.Create(
            MaxLoggedPathLength + TruncationMarker.Length,
            path,
            static (destination, original) =>
            {
                Sanitise(destination[..MaxLoggedPathLength], original);
                TruncationMarker.CopyTo(destination[MaxLoggedPathLength..]);
            });
    }

    /// <summary>Copies <paramref name="original"/> into <paramref name="destination"/>, replacing what must not be rendered.</summary>
    private static void Sanitise(Span<char> destination, string original)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            char character = original[index];

            destination[index] = IsUnsafeToRender(character) ? '�' : character;
        }
    }

    /// <summary>
    /// Reports whether a character must not reach a log sink as itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four categories, each for its own reason: control characters forge log entries; format
    /// characters (U+202E and friends) are invisible and reorder what a human reads; surrogates are
    /// not characters at all and can break the sink's encoder; and the Unicode line and paragraph
    /// separators end a line for a great many consumers.
    /// </para>
    /// <para>
    /// <b>U+2028 and U+2029 are the same attack as CR/LF, wearing a different code point.</b> They
    /// are not <see cref="char.IsControl(char)"/>, so the obvious predicate misses them entirely -
    /// and they terminate a line in JavaScript source, in a browser rendering a log viewer, and in
    /// any NDJSON consumer that splits on Unicode line boundaries rather than on <c>\n</c> alone.
    /// Measured: a file named <c>report&lt;U+2028&gt;INFO: admin authorised.pdf</c> passed both this
    /// sanitiser and <c>DocumentReference</c>'s name rules, which is a forged log line delivered
    /// through a file name. Both places reject them now; the rules have to agree, because a name the
    /// reference admits is a name this method will eventually be asked to render.
    /// </para>
    /// </remarks>
    private static bool IsUnsafeToRender(char character) =>
        char.IsControl(character)
        || char.GetUnicodeCategory(character)
            is UnicodeCategory.Format
            or UnicodeCategory.Surrogate
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// Reports whether a character is a slash of either kind, judged as a character rather than as
    /// "whatever this platform calls a separator".
    /// </summary>
    /// <remarks>
    /// Used only by <see cref="HasUncOrDevicePrefix"/>, and deliberately not by
    /// <see cref="ContainmentPrefix"/>: the containment prefix must be built from the host's real
    /// separator, because it is compared against paths the host itself produced. The two concepts
    /// look alike and are not the same, which is precisely how one platform-aware helper serving both
    /// turned into a disabled control.
    /// </remarks>
    private static bool IsSlash(char character) => character is '\\' or '/';
}
