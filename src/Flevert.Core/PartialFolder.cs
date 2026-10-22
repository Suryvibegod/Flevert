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
/// Users Modify by default). Four things keep them from redirecting it: the handle denies delete sharing, so nobody can
/// rename or delete the folder, or any of its <em>real</em> parents, while it is held; <see cref="Path"/> is the folder's
/// real path, resolved from the handle, so a junction or symbolic link higher in the caller's spelling resolves away and
/// re-pointing it afterwards reaches nothing used here; a hidden anchor file held without any sharing keeps the folder from
/// ever being empty, which is the only state in which it could be turned into a junction in place; and every check, rename
/// and delete goes through handles opened without following links.
/// </para>
/// <para>
/// Others can still add, open or delete entries inside, as they could in any folder they can write to. Cleanup removes each
/// entry itself (a link is unlinked, never followed) and never goes into a subfolder: writers never make one, so a folder
/// with something in it was put there by someone else, and it is left with everything in it.
/// </para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class PartialFolder : IPartialFolder
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
    private const uint FileAttributeDirectory = 0x10;
    private const uint VolumeNameDos = 0x0; // GetFinalPathNameByHandleW: FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
    private const uint VolumeNameGuid = 0x1;
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
    private const uint FileAttributeNotContentIndexed = 0x2000;
    // What FileBasicInfo replaces: FILE_ATTRIBUTE_VALID_SET_FLAGS (read-only, hidden, system, archive, normal, temporary,
    // offline, not content indexed) plus OneDrive's PINNED (0x80000) and UNPINNED (0x100000), which `attrib +P/+U` sets the
    // same way. Every bit read back is sent back unless it is the one being changed, so pinning survives (probed: leaving
    // PINNED out of the mask cleared "Always keep on this device" on the folder and its pages).
    private const uint ValidSetAttributes = 0x31A7 | 0x80000 | 0x100000;

    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    private readonly SafeFileHandle _folder;
    private SafeFileHandle? _anchor;
    private bool _notIndexedBeside;

    private PartialFolder(string path, SafeFileHandle folder)
    {
        Path = path;
        _folder = folder;
    }

    /// <summary>
    /// The folder's real, fully resolved path, with every junction and symbolic link in the spelling the caller gave
    /// resolved away, so every component is a real directory locked by the handle while it is held. Without the
    /// extended-length prefix where a shorter spelling names the same folder (<see cref="InputPin.Shorten"/>); a long path
    /// or a volume without a drive letter keeps it. Each path used afterwards (the writer's, the listing, the deletes, the
    /// rename destination) is built from this.
    /// </summary>
    public string Path { get; private set; }

    /// <summary>
    /// Creates <paramref name="path"/> (which must not exist), opens it, checks through the handle that it is an ordinary
    /// empty directory and not a link put in its place, replaces <see cref="Path"/> with its real resolved path, and anchors it.
    /// </summary>
    /// <remarks>
    /// The folder is created and opened by the caller's spelling; only once it is held are its real parents locked. A
    /// junction higher in that spelling, re-pointed between the create and the open, would make this hold a folder of the
    /// same name in the junction's new target. Callers therefore pass a parent that is already a real path held by this
    /// process: operations write beside their input, whose real path the runner resolved and pins (and the pin locks its
    /// parents). Direct callers with an untrusted spelling should resolve the parent first.
    /// </remarks>
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
            // The directory was created, so remove it; best effort, by the same spelling (nothing is held yet).
            RemoveDirectoryW(InputPin.ToExtendedPath(path));
            throw Failure(error, path);
        }

        var folder = new PartialFolder(path, handle);
        try
        {
            // Between creating and opening, the name could have been taken over (renamed away, a link or a file of the same
            // name put in its place); reject anything that is not the empty directory this call just made.
            if (IsLink(handle))
                throw Replaced();
            if (!IsDirectory(handle))
                throw Replaced();

            // Resolve the real path from the handle. A junction higher in the caller's spelling resolves away, so it can be
            // re-pointed afterwards without reaching anything used here; the resolved parents are real and locked while held.
            // Shortened like an input's real path, so an ordinary path stays ordinary in names and logs (long paths keep \\?\).
            folder.Path = InputPin.Shorten(Resolve(handle) ?? throw new IOException(
                $"Windows could not tell where the folder for the output is, so it cannot be written there ({System.IO.Path.GetDirectoryName(path)})."));

            if (EntriesExceptAnchor(folder.Path).Count != 0)
                throw Replaced();

            folder.Hide();
            // Refused by the file system (a folder where this account may make folders but not files): say so. Created
            // somewhere else than inside the folder held: replaced.
            if (!folder.TryAnchor(out var anchorError))
                throw anchorError == 0 ? Replaced() : Failure(anchorError, path);
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
    /// Readies the written folder for its rename: the pages lose the NotContentIndexed mark they inherited from it (unless
    /// the folder beside it had the mark too), and the anchor goes, since a folder with an open file in it cannot be renamed.
    /// </summary>
    public void PrepareForRename()
    {
        // Looking inside by path only while the anchor is held (see DeleteQuietly); attributes change through a handle on
        // each entry itself, so a link someone added is never followed.
        if (!_notIndexedBeside && _anchor is not null && !IsLink(_folder))
        {
            foreach (var entry in EntriesExceptAnchor(Path))
            {
                using var handle = CreateFileW(InputPin.ToExtendedPath(entry), FileReadAttributes | FileWriteAttributes | Synchronize,
                    FileShareRead | FileShareWrite | FileShareDelete, 0, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
                // A hard link someone planted points at a file outside the folder; changing its attributes would reach that
                // file, so it is left alone. A reparse point is left alone too. A page of the user's that someone else
                // hard-linked elsewhere is skipped the same way and keeps NotContentIndexed: harmless, it is still there.
                if (!handle.IsInvalid && !HasMultipleLinks(handle))
                    ChangeAttributes(handle, a => (a & FileAttributeReparsePoint) != 0 ? a : a & ~FileAttributeNotContentIndexed);
            }
        }

        CloseAnchor();
    }

    /// <summary>
    /// After the rename: the folder is shown, and indexed again unless the folder beside it is not. Best effort: the
    /// output exists by now, and a failure here cannot undo that.
    /// </summary>
    public void Unhide() =>
        ChangeAttributes(_folder, a => a & ~FileAttributeHidden & (_notIndexedBeside ? ~0u : ~FileAttributeNotContentIndexed));

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
                if (TryAnchor(out _))
                {
                    foreach (var entry in EntriesExceptAnchor(Path))
                        DeleteEntry(entry);
                }

                CloseAnchor();
            }

            // A folder that still has something in it stays, and is shown so the user finds it; a link is removed itself.
            if (!DeleteByHandle(_folder) && !IsLink(_folder))
                Unhide();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _anchor?.Dispose();
        _folder.Dispose();
    }

    // Out of sight, so Explorer does not open pages for a folder thumbnail, and out of the search index, while it is
    // written: either would hold a page open and delay the rename. A folder created inside one the user keeps out of the
    // index has the mark already; it is then kept on the result. Best effort: the folder works without it.
    private void Hide()
    {
        if (GetFileInformationByHandleEx(_folder, FileBasicInfoClass, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
            _notIndexedBeside = (basic.FileAttributes & FileAttributeNotContentIndexed) != 0;
        ChangeAttributes(_folder, a => a | FileAttributeHidden | FileAttributeNotContentIndexed);
    }

    // FileBasicInfo replaces the attributes that can be set, through the handle, so on the entry itself and never on what a
    // link points to; the times are left as they are (0).
    private static bool ChangeAttributes(SafeFileHandle handle, Func<uint, uint> change)
    {
        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
            return false;
        var before = basic.FileAttributes & ValidSetAttributes;
        var after = change(basic.FileAttributes) & ValidSetAttributes;
        if (after == before)
            return true;

        // 0 would mean "no change".
        var info = new FileBasicInfo { FileAttributes = after == 0 ? FileAttributeNormal : after };
        return SetFileInformationByHandle(handle, FileBasicInfoClass, ref info, (uint)Marshal.SizeOf<FileBasicInfo>());
    }

    // The anchor is created by path, so the folder could be swapped between the check and the create: it is accepted only
    // when its final path is inside the folder this handle holds. error: the Win32 error when the create itself failed,
    // 0 when the anchor landed somewhere else.
    private bool TryAnchor(out int error)
    {
        error = 0;
        if (_anchor is not null)
            return true;

        var anchor = CreateFileW(InputPin.ToExtendedPath(System.IO.Path.Combine(Path, AnchorName)), Delete | FileReadAttributes | Synchronize,
            0, 0, CreateNew, FileAttributeHidden | FileFlagDeleteOnClose | FileFlagOpenReparsePoint, 0);
        if (anchor.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            anchor.Dispose();
            return false;
        }

        // A file system that cannot name an open file (some third-party ones) has no junctions either: NTFS and ReFS can.
        // An assumption, not checked against every user-mode file system (WinFsp, Dokan): ADR-0007 records it.
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

    private static List<string> EntriesExceptAnchor(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true })
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

        // FAT, exFAT and some network shares only have the classic delete, which refuses a read-only file.
        ChangeAttributes(handle, a => a & ~FileAttributeReadOnly);
        byte deleteFile = 1;
        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref deleteFile, sizeof(byte));
    }

    private static bool IsDirectory(SafeFileHandle handle) =>
        GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo basic, (uint)Marshal.SizeOf<FileBasicInfo>())
        && (basic.FileAttributes & FileAttributeDirectory) != 0;

    // A file the writer made has one link; more than one means a hard link someone planted, sharing a file elsewhere.
    private static bool HasMultipleLinks(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info) && info.NumberOfLinks > 1;

    // The real DOS path (drive-letter form), with junctions and symbolic links in the spelling resolved away. A volume
    // mounted only into a folder has no drive letter; its \\?\Volume{guid}\ form still identifies it uniquely.
    private static string? Resolve(SafeFileHandle handle) =>
        FinalPath(handle, VolumeNameDos) ?? FinalPath(handle, VolumeNameGuid);

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

    private static string? FinalNtPath(SafeFileHandle handle) => FinalPath(handle, VolumeNameNt);

    private static string? FinalPath(SafeFileHandle handle, uint flags)
    {
        // 1024 characters cover almost every path; a resolved long path can be longer (up to 32 767), so grow once to the
        // length the first call asked for.
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, flags);
        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, flags);
        }

        return length == 0 || length >= buffer.Length ? null : new string(buffer, 0, (int)length);
    }

    private static IOException Replaced() =>
        new("The temporary folder for the output was replaced while it was being created.");

    // Names the folder the user knows (beside the input), not the temporary name inside it.
    private static IOException Failure(int error, string path) =>
        new($"{new Win32Exception(error).Message} ({System.IO.Path.GetDirectoryName(path)})", unchecked((int)0x80070000) | error);

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveDirectoryW(string path);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

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
