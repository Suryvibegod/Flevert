using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core;

/// <summary>
/// The temporary folder of <see cref="OutputPath.WriteDirectoryAtomicallyAsync(string, string, Func{string, CancellationToken, Task}, CancellationToken)"/>
/// on Linux, the counterpart of <see cref="PartialFolder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Created 0700, so while it is written nobody else can add, open or remove anything inside, and held open (without
/// following a link) from creation on. Its contents are listed and removed through that handle, so a link swapped in for
/// its name is never gone into, and it is renamed only while its name still leads to the folder held, with
/// <c>renameat2(RENAME_NOREPLACE)</c>, which fails on any name already taken.
/// </para>
/// <para>
/// Linux cannot lock a folder against being renamed, as the Windows handle does: someone who can write the parent folder
/// can still move it away between the check and the rename. The parent is beside the user's input, normally in their
/// home; docs/adr/0010-linux-nautilus-port.md records the gap.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class UnixPartialFolder : IPartialFolder
{
    private const uint OwnerOnly = 0x1C0; // 0700

    private readonly SafeFileHandle _folder;
    private readonly ulong _device;
    private readonly ulong _inode;

    private UnixPartialFolder(string path, SafeFileHandle folder, Libc.Statx stat)
    {
        Path = path;
        _folder = folder;
        _device = Device(stat);
        _inode = stat.Ino;
    }

    /// <summary>The folder's real path, links in the spelling given resolved away (from the handle).</summary>
    public string Path { get; private set; }

    /// <summary>The folder held, whatever its name leads to now: also from qpdf and the PDF worker, processes of this user.</summary>
    public string WritePath => $"/proc/{Environment.ProcessId}/fd/{_folder.DangerousGetHandle()}";

    /// <summary>Creates <paramref name="path"/>, which must not exist, opens it and checks it is the empty folder just made.</summary>
    public static UnixPartialFolder Create(string path)
    {
        if (Libc.MakeDirectory(path, OwnerOnly) != 0)
            throw Failure(Marshal.GetLastPInvokeError(), path);

        var handle = Libc.Open(path, Libc.ORdonly | Libc.ODirectory | Libc.ONofollow | Libc.OCloexec);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            // Nothing is held yet: what is at the name may not be the folder made above, so it is left alone.
            throw Failure(error, path);
        }

        if (!Libc.TryStat(handle, out var stat) || stat.Type != Libc.SIfDir || stat.Uid != Libc.EffectiveUserId
            || Libc.PathOf(handle) is not { } real)
        {
            handle.Dispose();
            throw Replaced();
        }

        var folder = new UnixPartialFolder(real, handle, stat);
        if (folder.Entries().Count != 0)
        {
            folder.Dispose();
            throw Replaced();
        }

        return folder;
    }

    // Nothing to let go of: Linux renames a folder with files open in it.
    public void PrepareForRename()
    {
    }

    /// <summary>After the rename: the permissions any new folder gets (0777 less the umask). Best effort.</summary>
    public void Unhide()
    {
        try
        {
            File.SetUnixFileMode(Libc.HandlePath(_folder), (UnixFileMode)0x1FF & ~Libc.Umask());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Renames the folder to <paramref name="destination"/>, never replacing anything there.</summary>
    /// <returns>0, or the errno: EEXIST or ENOTEMPTY when the name is taken.</returns>
    /// <exception cref="IOException">The name no longer leads to the folder held.</exception>
    public int TryRenameTo(string destination)
    {
        if (IsLink())
            throw Replaced();

        if (Libc.Rename(Path, destination, Libc.RenameNoReplace) == 0)
        {
            Path = destination;
            return 0;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is not (Libc.EInval or Libc.ENoSys))
            return error;

        // A file system without RENAME_NOREPLACE (some network and FUSE ones): check, then rename. The check and the rename
        // are two steps; the name is beside the user's own input.
        if (System.IO.Path.Exists(destination) || Libc.TryStatNoFollow(destination, out _))
            return Libc.EExist;
        if (Libc.Rename(Path, destination, 0) != 0)
            return Marshal.GetLastPInvokeError();
        Path = destination;
        return 0;
    }

    /// <summary>Whether the folder's name no longer leads to the folder held: a link, or anything else, is there now.</summary>
    public bool IsLink() =>
        !Libc.TryStatNoFollow(Path, out var stat) || stat.Type != Libc.SIfDir || Device(stat) != _device || stat.Ino != _inode;

    /// <summary>
    /// Deletes what the writer left and the folder, never following a link: entries are removed through the handle (a link
    /// is unlinked itself, a folder inside only when empty), and the folder only where its name still leads to it. A link
    /// put in place of the folder is removed itself. Never throws.
    /// </summary>
    public void DeleteQuietly()
    {
        try
        {
            foreach (var name in Entries())
            {
                if (Libc.Unlink(_folder, name, 0) != 0 && Marshal.GetLastPInvokeError() == Libc.EIsDir)
                    _ = Libc.Unlink(_folder, name, Libc.AtRemoveDir);
            }

            if (IsLink())
            {
                // Moved away and something put at the name: remove a link there (only the link), then the folder held
                // where it is now, if it is still there.
                if (Libc.TryStatNoFollow(Path, out var there) && there.Type == Libc.SIfLnk)
                    _ = Libc.Unlink(null, Path, 0);
                if (Libc.PathOf(_folder) is { } now && Libc.TryStatNoFollow(now, out var moved) && Device(moved) == _device && moved.Ino == _inode)
                    _ = Libc.Unlink(null, now, Libc.AtRemoveDir);
                return;
            }

            // A folder that still has something in it stays, with the permissions of any folder, so the user can open it.
            if (Libc.Unlink(null, Path, Libc.AtRemoveDir) != 0)
                Unhide();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => _folder.Dispose();

    // Listed through the handle, so it is this folder's whatever happened to its name.
    private List<string> Entries() =>
        [.. Directory.EnumerateFileSystemEntries(Libc.HandlePath(_folder), "*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true })
            .Select(entry => System.IO.Path.GetFileName(entry))];

    private static ulong Device(Libc.Statx stat) => ((ulong)stat.DevMajor << 32) | stat.DevMinor;

    private static IOException Replaced() =>
        new("The temporary folder for the output was replaced while it was being created or written.");

    // Names the folder the user knows (beside the input), not the temporary name inside it.
    private static IOException Failure(int error, string path) =>
        new($"{Libc.ErrorMessage(error)} ({System.IO.Path.GetDirectoryName(path)})");
}
