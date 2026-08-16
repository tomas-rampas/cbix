using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Cbix.Core.Ingest;

/// <summary>
/// Reads the number of hard links pointing at an already-open file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Path resolution - normalising <c>..</c>, following symbolic links and
/// junctions to their final targets - answers "where does this name lead". It cannot answer "does
/// anything else lead here", and a hard link is exactly that: a second, equal name for one file,
/// with no link to resolve and nothing in the path to notice. A hard link created inside the ingest
/// root against a file outside it is therefore invisible to every check the path can support, and
/// the only cheap primitive that sees it is the link count on the opened file itself.
/// </para>
/// <para>
/// <b>Read from the handle, not the path.</b> The count is taken from the handle the service is
/// already holding open and about to hash, so it describes the file whose bytes are actually read.
/// A path-based query would be a second lookup with its own race against the first.
/// </para>
/// <para>
/// <b>Unsupported platforms fail closed.</b> Windows and Linux are implemented. Anywhere else this
/// throws rather than returning a reassuring 1: silently skipping a security control on a platform
/// nobody verified it on is how a control becomes decorative. Adding macOS means adding its
/// <c>fstat</c> layout and the probe test that proves it, not relaxing this.
/// </para>
/// </remarks>
internal static class FileLinkCount
{
    /// <summary>
    /// Byte offset of <c>nNumberOfLinks</c> within <c>BY_HANDLE_FILE_INFORMATION</c>: four bytes of
    /// attributes, three eight-byte FILETIMEs, then the volume serial and the two file-size halves.
    /// </summary>
    private const int WindowsLinkCountOffset = 40;

    /// <summary>Size of <c>BY_HANDLE_FILE_INFORMATION</c> in bytes.</summary>
    private const int WindowsFileInformationSize = 52;

    /// <summary>Byte offset of <c>stx_nlink</c> within <c>struct statx</c>: <c>stx_mask</c>, <c>stx_blksize</c>, <c>stx_attributes</c>.</summary>
    private const int LinuxLinkCountOffset = 16;

    /// <summary>Size of <c>struct statx</c>, which the kernel fills in full.</summary>
    private const int LinuxStatxSize = 256;

    /// <summary><c>AT_EMPTY_PATH</c>: with an empty path, operate on the descriptor itself.</summary>
    private const int LinuxAtEmptyPath = 0x1000;

    /// <summary><c>STATX_NLINK</c>: the only field this call asks the kernel for.</summary>
    private const uint LinuxStatxNlink = 0x0000_0004;

    /// <summary>
    /// Returns the number of hard links naming the file behind <paramref name="handle"/>.
    /// </summary>
    /// <param name="handle">An open handle to the file, held for the duration of the call.</param>
    /// <returns>The link count; 1 means this file has exactly one name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handle"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The platform call failed.</exception>
    /// <exception cref="PlatformNotSupportedException">The host platform has no implementation here.</exception>
    public static uint Read(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (OperatingSystem.IsWindows())
        {
            return ReadWindows(handle);
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadLinux(handle);
        }

        throw new PlatformNotSupportedException(
            "Ingest cannot verify a file's hard-link count on this platform, so it cannot prove the file is reachable only from inside the ingest root.");
    }

    private static uint ReadWindows(SafeFileHandle handle)
    {
        Span<byte> information = stackalloc byte[WindowsFileInformationSize];

        if (!GetFileInformationByHandle(handle, ref MemoryMarshal.GetReference(information)))
        {
            throw new IOException(
                "Could not read file information for the submitted document.",
                Marshal.GetLastPInvokeError());
        }

        return MemoryMarshal.Read<uint>(information[WindowsLinkCountOffset..]);
    }

    private static uint ReadLinux(SafeFileHandle handle)
    {
        // statx is used rather than fstat because its structure layout is fixed by the kernel ABI
        // and identical on every architecture, while struct stat's field order and widths differ
        // between x86-64 and arm64 - a difference that would be invisible until it silently read
        // the wrong four bytes on the wrong machine.
        Span<byte> statx = stackalloc byte[LinuxStatxSize];
        statx.Clear();

        ReadOnlySpan<byte> emptyPath = [0];

        bool handleReferenced = false;
        try
        {
            handle.DangerousAddRef(ref handleReferenced);
            int descriptor = (int)handle.DangerousGetHandle();

            int result;
            try
            {
                result = Statx(
                    descriptor,
                    in MemoryMarshal.GetReference(emptyPath),
                    LinuxAtEmptyPath,
                    LinuxStatxNlink,
                    ref MemoryMarshal.GetReference(statx));
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new PlatformNotSupportedException(
                    "This host's C library does not expose statx, so ingest cannot verify a file's hard-link count.",
                    error);
            }

            if (result != 0)
            {
                throw new IOException(
                    "Could not read file information for the submitted document.",
                    Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            if (handleReferenced)
            {
                handle.DangerousRelease();
            }
        }

        // stx_mask reports which fields the kernel actually filled. An unset STATX_NLINK bit means
        // the zero sitting at that offset is absence, not a link count - fail closed rather than
        // read it as "fewer links than one".
        uint filledMask = MemoryMarshal.Read<uint>(statx);
        if ((filledMask & LinuxStatxNlink) == 0)
        {
            throw new IOException("The kernel did not report a hard-link count for the submitted document.");
        }

        return MemoryMarshal.Read<uint>(statx[LinuxLinkCountOffset..]);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, ref byte fileInformation);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(int directoryDescriptor, in byte path, int flags, uint mask, ref byte buffer);
}
