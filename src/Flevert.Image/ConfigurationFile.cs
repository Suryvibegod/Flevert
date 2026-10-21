using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Image;

/// <summary>
/// Puts one of ImageMagick's configuration files in place, given the exact bytes it must hold. Several flevert.exe
/// processes can start at the same moment (one batch leader per operation), and ImageMagick opens these files without
/// delete sharing while it loads them, so a file another process is reading cannot be replaced (Windows refuses with
/// "Access to the path is denied"). A file that already holds the exact bytes is therefore kept as it is, but only when
/// nothing but this user, SYSTEM and Administrators could have changed it; anything else is replaced atomically, waiting
/// for readers to let go.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ConfigurationFile
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeReparsePoint = 0x400;
    private const int GenericAll = 0x10000000;
    private const int GenericWrite = 0x40000000;

    /// <summary>Makes <paramref name="path"/> hold exactly <paramref name="expected"/>, in a file of its own.</summary>
    /// <param name="patience">How long, counted on <paramref name="elapsed"/>, to wait for other processes to release it.</param>
    /// <exception cref="IOException">The file holds something else and could not be replaced in time.</exception>
    public static void Ensure(string path, byte[] expected, Stopwatch elapsed, TimeSpan patience)
    {
        if (IsTrustedCopy(path, expected))
            return;

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temp, expected);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Usually another flevert.exe loading this file; it may also have just put the right bytes there.
                    if (IsTrustedCopy(path, expected))
                        return;
                    if (elapsed.Elapsed >= patience)
                        throw new IOException($"Could not replace ImageMagick's configuration file {path}.", ex);
                    Thread.Sleep(Math.Min(10 * attempt, 100));
                }
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a plain file (no link of any kind) holding exactly <paramref name="expected"/>,
    /// owned by and writable only by this user, SYSTEM or Administrators. Reads with read and delete sharing, so other
    /// processes can still load or replace it meanwhile.
    /// </summary>
    internal static bool IsTrustedCopy(string path, byte[] expected)
    {
        // Not following links: a symbolic link or junction here could lead to a file other accounts can change.
        using var handle = CreateFileW(path, GenericRead, FileShareRead | FileShareDelete, 0, OpenExisting, FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
            return false;
        // A hard link keeps the permissions of the file it shares, which may not be this folder's.
        if (!GetFileInformationByHandle(handle, out var info)
            || (info.FileAttributes & FileAttributeReparsePoint) != 0
            || info.NumberOfLinks != 1
            || info.FileSizeHigh != 0
            || info.FileSizeLow != (uint)expected.Length)
        {
            return false;
        }

        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 0);
        if (!OnlyTrustedAccountsCanChange(stream.GetAccessControl()))
            return false;

        var actual = new byte[expected.Length];
        try
        {
            stream.ReadExactly(actual);
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        return actual.AsSpan().SequenceEqual(expected);
    }

    private static bool OnlyTrustedAccountsCanChange(FileSecurity security)
    {
        using var me = WindowsIdentity.GetCurrent();
        SecurityIdentifier[] trusted =
        [
            me.User!,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        ];

        // The owner can always rewrite the permissions.
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner))
            return false;

        const FileSystemRights change = FileSystemRights.WriteData | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
            | (FileSystemRights)(GenericAll | GenericWrite); // an ACE may hold generic rights unmapped
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & change) != 0
                && !trusted.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}
