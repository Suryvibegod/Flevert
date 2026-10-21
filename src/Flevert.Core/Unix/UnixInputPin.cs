using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core;

/// <summary>
/// The Linux counterpart of <see cref="InputPin"/>: opens an input without following a link and keeps it open while an
/// operation works on it, giving the operation the file's real location from the handle. Opened with <c>O_PATH</c>, so
/// nothing is read and opening a FIFO or a device neither blocks nor has an effect. Linux has no mandatory locks: unlike
/// on Windows, the file can still be renamed or replaced while it is pinned (docs/adr/0010-linux-nautilus-port.md).
/// </summary>
[SupportedOSPlatform("linux")]
internal static class UnixInputPin
{
    /// <returns>A handle to keep open while processing, or <c>null</c> with <paramref name="reason"/> set.</returns>
    public static SafeFileHandle? TryPin(string path, out string? reason, out string realPath)
    {
        realPath = path;
        var handle = Libc.Open(path, Libc.OPath | Libc.ONofollow | Libc.OCloexec);
        if (handle.IsInvalid)
            return Fail(handle, Libc.ErrorMessage(Marshal.GetLastPInvokeError()), out reason);

        if (!Libc.TryStat(handle, out var stat))
            return Fail(handle, Libc.ErrorMessage(Marshal.GetLastPInvokeError()), out reason);

        // A link in a shared folder could point at a private file and leak a converted copy of it there.
        if (stat.Type == Libc.SIfLnk)
            return Fail(handle, "Symbolic links are not followed.", out reason);

        // Whatever spelling got this far, only an ordinary file is processed (not a FIFO, socket, device or folder).
        if (stat.Type != Libc.SIfReg)
            return Fail(handle, "Only files on disk are accepted.", out reason);

        // A hard link to a file another account cannot read leaks the same way as a symbolic link.
        if (stat.Nlink > 1)
            return Fail(handle, "Files with more than one hard link are not processed.", out reason);
        if (stat.Nlink == 0)
            return Fail(handle, "The file was deleted.", out reason);

        if (Libc.PathOf(handle) is not { } real)
            return Fail(handle, "Flevert could not tell where this file is.", out reason);

        realPath = real;
        reason = null;
        return handle;
    }

    private static SafeFileHandle? Fail(SafeFileHandle handle, string why, out string? reason)
    {
        handle.Dispose();
        reason = why;
        return null;
    }
}
