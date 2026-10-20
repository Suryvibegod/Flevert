using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core;

/// <summary>
/// The temporary folder <see cref="OutputPath.WriteDirectoryAtomicallyAsync(string, string, Func{string, CancellationToken, Task}, CancellationToken)"/>
/// hands to an operation, held open from creation until it is renamed or deleted (ADR-0007, "Holding the folder").
/// </summary>
/// <remarks>
/// <para>
/// The folder sits beside the input, where other accounts may have write access (a second NTFS drive gives Authenticated
/// Users Modify by default). Three things keep them from redirecting it: the handle denies delete sharing, so nobody can
/// rename or delete the folder (or any folder above it) while it is held; a hidden anchor file held without any sharing
/// keeps it from ever being empty, which is the only state in which a folder can be turned into a junction in place; and
/// every check, rename and delete goes through handles opened without following links.
/// </para>
/// <para>
/// Others can still add, open or delete entries inside, as they could in any folder they can write to. Cleanup removes each
/// entry itself (a link is unlinked, never followed) and never goes into a subfolder: writers never make one, so a folder
/// with something in it was put there by someone else, and it is left with everything in it.
/// </para>
/// </remarks>
internal sealed partial class PartialFolder : IDisposable
{
    /// <summary>The anchor's name; writers must not create a file with this name.</summary>
    internal const string AnchorName = ".flevert.lock";

    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x80;
    private const uint FileWriteAttributes = 0x100;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileAttributeHidden = 0x2;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagDeleteOnClose = 0x04000000;
    private const uint NameSurrogateBit = 0x20000000; // junctions, symbolic links, mount points
    private const uint VolumeNameNt = 0x2;

    private const int FileBasicInfoClass = 0;
    private const int FileRenameInfoClass = 3;
    private const int FileDispositionInfoClass = 4;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileDispositionInfoExClass = 21;
    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2; // the name goes at once, even while another program has it open
    private const uint DispositionIgnoreReadOnly = 0x10;
    private const uint FileAttributeReadOnly = 0x1;
    private const uint FileAttributeNormal = 0x80;

    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    private readonly SafeFileHandle _folder;
    private SafeFileHandle? _anchor;

    private PartialFolder(string path, SafeFileHandle folder)
    {
        Path = path;
        _folder = folder;
    }

    /// <summary>The folder's path, as given to <see cref="Create"/> and then as renamed.</summary>
    public string Path { get; private set; }

    /// <summary>
    /// Creates <paramref name="path"/> (which must not exist), opens it, checks through the handle that it is the folder just
    /// made and not a link put in its place, and anchors it.
    /// </summary>
    public static PartialFolder Create(string path)
    {
        if (!CreateDirectoryW(InputPin.ToExtendedPath(path), 0))
            throw Failure(Marshal.GetLastPInvokeError(), path);

        // DELETE to rename and delete it through this handle; read and write sharing so writers and scanners work inside.
        var handle = CreateFileW(InputPin.ToExtendedPath(path), Delete | FileReadAttributes | FileWriteAttributes | Synchronize,
            FileShareRead | FileShareWrite, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure(error, path);
        }

        var folder = new PartialFolder(path, handle);
        try
        {
            // Between creating and opening, the name could have been taken over (renamed away, a link put in its place).
            if (IsLink(handle))
                throw Replaced();
            if (!folder.TryAnchor())
                throw Replaced();
            return folder;
        }
        catch
        {
            folder.DeleteQuietly();
            folder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Renames the folder through its handle to <paramref name="destination"/> (beside it), never replacing anything there.
    /// </summary>
    /// <returns>0, or the Win32 error: ERROR_ALREADY_EXISTS when the name is taken, ERROR_ACCESS_DENIED while another
    /// program has a file inside open.</returns>
    public int TryRenameTo(string destination)
    {
        // A folder with an open file in it cannot be renamed, the anchor included.
        CloseAnchor();

        // A full path: a bare name would be taken relative to the process's current directory (FILE_RENAME_INFO). The
        // extended form keeps long paths working and is passed on unchanged, so the name must already be a plain one.
        var name = InputPin.ToExtendedPath(destination);
        var nameOffset = (2 * IntPtr.Size) + sizeof(uint);
        var headerSize = (nameOffset + sizeof(char) + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size; // sizeof(FILE_RENAME_INFO)
        var info = new byte[headerSize + (name.Length * sizeof(char))];
        // ReplaceIfExists = FALSE (offset 0) and RootDirectory = NULL (offset IntPtr.Size) are already zero.
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(2 * IntPtr.Size), (uint)(name.Length * sizeof(char)));
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(info.AsSpan(nameOffset));
        if (!SetFileInformationByHandle(_folder, FileRenameInfoClass, info, (uint)info.Length))
            return Marshal.GetLastPInvokeError();

        Path = destination;
        return 0;
    }

    /// <summary>
    /// Whether the folder held is now a link: renamed through the handle, so a folder emptied and turned into a junction
    /// after the anchor went would have been renamed as that junction.
    /// </summary>
    public bool IsLink() => IsLink(_folder);

    /// <summary>
    /// Deletes the folder and what the writer left in it, without ever following a link: if the folder itself became a
    /// link, only the link is removed. Never throws: a failed cleanup must not hide the original error.
    /// </summary>
    public void DeleteQuietly()
    {
        try
        {
            if (!IsLink(_folder))
            {
                // Only with the anchor held is looking inside by path safe: an empty folder could become a junction to
                // someone else's files between the check above and the listing.
                if (TryAnchor())
                {
                    foreach (var entry in EntriesExceptAnchor())
                        DeleteEntry(entry);
                }

                CloseAnchor();
            }

            // A folder that still has something in it stays; a link is removed itself.
            DeleteByHandle(_folder);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _anchor?.Dispose();
        _folder.Dispose();
    }

    // The anchor is created by path, so the folder could be swapped between the check and the create: it is accepted only
    // when its final path is inside the folder this handle holds.
    private bool TryAnchor()
    {
        if (_anchor is not null)
            return true;

        var anchor = CreateFileW(InputPin.ToExtendedPath(System.IO.Path.Combine(Path, AnchorName)), Delete | FileReadAttributes | Synchronize,
            0, 0, CreateNew, FileAttributeHidden | FileFlagDeleteOnClose | FileFlagOpenReparsePoint, 0);
        if (anchor.IsInvalid)
        {
            anchor.Dispose();
            return false;
        }

        // A file system that cannot name an open file (some third-party ones) has no junctions either: NTFS and ReFS can.
        if (!IsLink(_folder)
            && (FinalNtPath(_folder), FinalNtPath(anchor)) is var (folder, inside)
            && (folder is null || inside is null || string.Equals(inside, folder + @"\" + AnchorName, StringComparison.OrdinalIgnoreCase)))
        {
            _anchor = anchor;
            return true;
        }

        anchor.Dispose(); // deleted on close: wherever it landed, it is this process's own new file
        return false;
    }

    // Deleted on close.
    private void CloseAnchor()
    {
        _anchor?.Dispose();
        _anchor = null;
    }

    private List<string> EntriesExceptAnchor() =>
        [.. Directory.EnumerateFileSystemEntries(Path, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true })
            .Where(entry => !string.Equals(System.IO.Path.GetFileName(entry), AnchorName, StringComparison.OrdinalIgnoreCase))];

    // Opened without following a link, so a link is removed itself; a folder is removed only when empty, since the delete
    // fails on one that is not; nothing is ever gone into.
    private static void DeleteEntry(string path)
    {
        using var entry = CreateFileW(InputPin.ToExtendedPath(path), Delete | FileReadAttributes | FileWriteAttributes | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (!entry.IsInvalid)
            DeleteByHandle(entry);
    }

    // Removed when the handle closes. POSIX semantics drop the name at once even if a scanner still has the file open, so
    // the folder can go too; the read-only attribute is ignored rather than cleared.
    private static bool DeleteByHandle(SafeFileHandle handle)
    {
        var flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly;
        if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref flags, sizeof(uint)))
            return true;
        if (Marshal.GetLastPInvokeError() is not (ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction))
            return false;

        // FAT, exFAT and some network shares only have the classic delete, which refuses a read-only file. The attribute
        // is changed through the handle, on the entry itself, never on what a link points to.
        if (GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>())
            && (basic.FileAttributes & FileAttributeReadOnly) != 0)
        {
            var attributes = basic.FileAttributes & ~FileAttributeReadOnly;
            basic = default;
            basic.FileAttributes = attributes == 0 ? FileAttributeNormal : attributes;
            SetFileInformationByHandle(handle, FileBasicInfoClass, ref basic, (uint)Marshal.SizeOf<FileBasicInfo>());
        }

        byte deleteFile = 1;
        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref deleteFile, sizeof(byte));
    }

    // Junctions, symbolic links and mount points are name surrogates; a cloud placeholder (OneDrive) is a reparse point too
    // but still the folder itself.
    private static bool IsLink(SafeFileHandle handle)
    {
        if (GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out FileAttributeTagInfo tag, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            return (tag.FileAttributes & FileAttributeReparsePoint) != 0 && (tag.ReparseTag & NameSurrogateBit) != 0;

        // A file system without the tag query has no links either; any reparse point it reports counts as one.
        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
            throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        return (basic.FileAttributes & FileAttributeReparsePoint) != 0;
    }

    private static string? FinalNtPath(SafeFileHandle handle)
    {
        var buffer = new char[short.MaxValue];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, VolumeNameNt);
        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    private static IOException Replaced() =>
        new("The temporary folder for the output was replaced while it was being created.");

    private static IOException Failure(int error, string path) =>
        new($"{new Win32Exception(error).Message} ({System.IO.Path.GetFileName(path)})", unchecked((int)0x80070000) | error);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    // Times left at 0 are not changed by SetFileInformationByHandle; FileAttributes 0 means "no change" too.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryW(string path, nint security);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileBasicInfo info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FileBasicInfo info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref uint info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte[] info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
