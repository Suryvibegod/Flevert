using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core;

/// <summary>
/// Opens an input without following links and keeps it open, read-shared only, while an operation works on it. Without
/// delete sharing nobody can rename, delete or replace the file meanwhile, and the operation is given the file's real
/// location from the handle, so a junction swapped in a parent folder cannot redirect what is read or where output goes.
/// </summary>
internal static partial class InputPin
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    // If a path still reaches a named pipe, its server may only identify the user, never impersonate them. The bit is
    // shared with FILE_FLAG_OPEN_NO_RECALL; the pin reads no data, so a cloud placeholder is not downloaded by it (the
    // operation's own open recalls it). Behaviour on OneDrive placeholders is still to be checked by hand.
    private const uint SecuritySqosPresent = 0x00100000;
    private const uint SecurityIdentification = 0x00010000;
    private const uint VolumeNameGuid = 0x1;
    private const int FileAttributeTagInfoClass = 9;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint NameSurrogateBit = 0x20000000; // symlinks, mount points and other name-redirecting reparse tags
    private const int MaxPath = 260;
    private const uint FileTypeDisk = 1;

    /// <returns>A handle to keep open while processing, or <c>null</c> with <paramref name="reason"/> set.</returns>
    public static SafeFileHandle? TryPin(string path, out string? reason, out string realPath)
    {
        realPath = path;
        var handle = CreateFileW(ToExtendedPath(path), GenericRead, FileShareRead, 0, OpenExisting,
            FileFlagOpenReparsePoint | SecuritySqosPresent | SecurityIdentification, 0);
        if (handle.IsInvalid)
            return Fail(handle, new Win32Exception(Marshal.GetLastPInvokeError()).Message, out reason);

        // Whatever spelling got this far, only an ordinary file on disk is processed (not a pipe or device).
        if (GetFileType(handle) != FileTypeDisk)
            return Fail(handle, "Only files on disk are accepted.", out reason);

        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var tag, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            return Fail(handle, new Win32Exception(Marshal.GetLastPInvokeError()).Message, out reason);

        // A link in a shared folder could point at a private file and leak a converted copy of it there.
        // Cloud placeholders (OneDrive) are reparse points too, but not name surrogates, and still work.
        if ((tag.FileAttributes & FileAttributeReparsePoint) != 0 && (tag.ReparseTag & NameSurrogateBit) != 0)
            return Fail(handle, "Symbolic links are not followed.", out reason);

        // A hard link to a file another account cannot read is not a reparse point but leaks the same way.
        if (!GetFileInformationByHandle(handle, out var info))
            return Fail(handle, new Win32Exception(Marshal.GetLastPInvokeError()).Message, out reason);
        if (info.NumberOfLinks > 1)
            return Fail(handle, "Files with more than one hard link are not processed.", out reason);

        if (FinalPath(handle) is not { } final)
            return Fail(handle, new Win32Exception(Marshal.GetLastPInvokeError()).Message, out reason);

        // A link can lead into a folder named "x." although the path given was clean; the operation reopens the real
        // path, and anything that normalises it would read "x\…", which was never pinned or checked.
        if (InputPath.HasNameEndingInDotOrSpace(final))
            return Fail(handle, InputPath.NameEndsInDotOrSpaceMessage, out reason);
        if (InputPath.HasStreamName(final))
            return Fail(handle, InputPath.StreamNameMessage, out reason);

        realPath = final;
        reason = null;
        return handle;
    }

    private static SafeFileHandle? Fail(SafeFileHandle handle, string why, out string? reason)
    {
        handle.Dispose();
        reason = why;
        return null;
    }

    // Without the prefix CreateFileW is limited to MAX_PATH unless the machine opted in to long paths.
    internal static string ToExtendedPath(string path) => path switch
    {
        _ when path.StartsWith(@"\\?\", StringComparison.Ordinal) => path,
        _ when path.StartsWith(@"\\", StringComparison.Ordinal) => @"\\?\UNC\" + path[2..],
        _ => @"\\?\" + path,
    };

    private static string? FinalPath(SafeFileHandle handle)
    {
        // The drive-letter form fails for a volume mounted only into a folder; its \\?\Volume{guid}\ form still opens.
        var final = FinalPath(handle, flags: 0) ?? FinalPath(handle, VolumeNameGuid);
        return final is null ? null : Shorten(final);
    }

    /// <summary>
    /// Drops the extended-path prefix where it is not needed, so ordinary paths look ordinary in names and logs and tools
    /// launched later with them (ffmpeg, qpdf) can open them. Only where Win32 normalisation leaves the shorter path
    /// unchanged: otherwise it would name something else (a device such as NUL on Windows 10, a trailing dot or space).
    /// </summary>
    internal static string Shorten(string final)
    {
        string shorter;
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            shorter = @"\\" + final[8..];
        else if (final.StartsWith(@"\\?\", StringComparison.Ordinal) && final.Length > 6 && final[5] == ':')
            shorter = final[4..];
        else
            return final; // \\?\Volume{guid}\ has no shorter form

        return shorter.Length < MaxPath && string.Equals(Path.GetFullPath(shorter), shorter, StringComparison.Ordinal)
            ? shorter
            : final;
    }

    private static string? FinalPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new char[short.MaxValue];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, flags);
        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    // FILETIME is two DWORDs: 4-byte packing keeps the native layout (no padding before the times).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(SafeFileHandle file);
}
