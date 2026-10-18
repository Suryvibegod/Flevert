using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ConvertAnything.Core;

/// <summary>
/// Opens an input without following links and keeps it open, read-shared only, while an operation works on it.
/// Without delete sharing nobody can rename, delete or replace the file in the meantime, so the operation reopening the
/// same path is guaranteed to read the file that was checked.
/// </summary>
internal static partial class InputPin
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileAttributeTagInfoClass = 9;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint ReparseTagSymlink = 0xA000000C;
    private const uint ReparseTagMountPoint = 0xA0000003;

    /// <returns>A handle to keep open while processing, or <c>null</c> with <paramref name="reason"/> set.</returns>
    public static SafeFileHandle? TryPin(string path, out string? reason)
    {
        var handle = CreateFileW(path, GenericRead, FileShareRead, 0, OpenExisting, FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            handle.Dispose();
            return null;
        }

        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            reason = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            handle.Dispose();
            return null;
        }

        // A link in a shared folder could point at a private file and leak a converted copy of it there.
        // Cloud placeholders (OneDrive) are reparse points too, with other tags, and still work.
        if ((info.FileAttributes & FileAttributeReparsePoint) != 0 && info.ReparseTag is ReparseTagSymlink or ReparseTagMountPoint)
        {
            reason = "Symbolic links are not followed.";
            handle.Dispose();
            return null;
        }

        reason = null;
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);
}
