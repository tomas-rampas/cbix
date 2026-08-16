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
    /// Reports whether a path begins with two directory separators, which on Windows means a UNC
    /// share (<c>\\server\share</c>) or a device namespace path (<c>\\?\</c>, <c>\\.\</c>).
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
    /// Both separator characters are accepted as the leading pair because Windows treats
    /// <c>//server/share</c> exactly as it treats <c>\\server\share</c>. On Unix a leading <c>//</c>
    /// is implementation-defined and normalises to <c>/</c>, so refusing it costs nothing real.
    /// </para>
    /// </remarks>
    public static bool HasUncOrDevicePrefix(string path) =>
        path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);

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

    /// <summary>Maximum length of a path rendered into a log event.</summary>
    private const int MaxLoggedPathLength = 400;

    /// <summary>
    /// Renders a path safely for a log event: control and Unicode format characters replaced, length
    /// bounded.
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
    /// The value is replaced, never dropped: an operator needs to see that something was there, and
    /// a replacement character in an alert is itself a strong signal about the submission.
    /// </para>
    /// </remarks>
    public static string ForLog(string path)
    {
        ReadOnlySpan<char> source = path.Length > MaxLoggedPathLength
            ? path.AsSpan(0, MaxLoggedPathLength)
            : path;

        return string.Create(source.Length, path, static (destination, original) =>
        {
            for (int index = 0; index < destination.Length; index++)
            {
                char character = original[index];

                destination[index] = char.IsControl(character)
                    || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format
                        ? '�'
                        : character;
            }
        });
    }

    private static bool IsSeparator(char character) =>
        character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;
}
