using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ConvertAnything.Core.Menu;

public static partial class InstallLocation
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint DriveFixed = 3;
    private const uint FilePersistentAcls = 0x8;

    private const FileSystemRights GenericAll = (FileSystemRights)0x10000000;
    private const FileSystemRights GenericWrite = (FileSystemRights)0x40000000;

    // Rights that let someone change or replace an installed file or folder.
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | GenericAll | GenericWrite;

    // Rights on a parent folder that let someone rename the install folder, or the parent itself, away and rebuild the
    // path with a planted ca.exe. Modify grants DELETE on the parent, which is enough.
    private const FileSystemRights ParentRights =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership | GenericAll;

    /// <summary>
    /// The menu runs ca.exe as whoever clicks it. If another account could replace ca.exe or a DLL beside it, or swap the
    /// whole folder, that account could run code as this user. Checks owners and DACLs of the folder, everything in it,
    /// and every parent folder, and refuses a folder reached through a junction or symbolic link.
    /// </summary>
    public static bool IsWritableOnlyByOwnerAndAdmins(string directory) => IsWritableOnlyByOwnerAndAdmins(directory, stopAt: null);

    /// <param name="stopAt">Tests only: stop the parent walk before this folder, so a test can build the whole chain.</param>
    internal static bool IsWritableOnlyByOwnerAndAdmins(string directory, string? stopAt)
    {
        // A folder or file whose permissions cannot be read cannot be shown to be safe; refusing gives the installer
        // the "folder refused" exit code instead of a generic failure.
        try
        {
            return Check(directory, stopAt);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool Check(string directory, string? stopAt)
    {
        using var me = WindowsIdentity.GetCurrent();
        var trusted = new HashSet<SecurityIdentifier>
        {
            me.User!,
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.CreatorOwnerSid, null),
            new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
        };

        var root = new DirectoryInfo(directory);
        if (!IsOnLocalFixedDriveAsWritten(root.FullName))
            return false;
        if (IsLink(root) || !IsSafe(root.GetAccessControl(), WriteRights, trusted, countInheritOnly: true))
            return false;

        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                return false; // its ACL check would follow the link to a target whose parents are never checked
            FileSystemSecurity acl = entry is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
            if (!IsSafe(acl, WriteRights, trusted, countInheritOnly: true))
                return false;
        }

        // A parent's inherit-only entries are not held against it: what they passed down is in the ACLs checked above
        // (C:\ itself grants Authenticated Users inherit-only Modify for new folders).
        for (var parent = root.Parent; parent is not null; parent = parent.Parent)
        {
            if (stopAt is not null && string.Equals(parent.FullName.TrimEnd('\\'), stopAt.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                break;
            if (IsLink(parent) || !IsSafe(parent.GetAccessControl(), ParentRights, trusted, countInheritOnly: false))
                return false;
        }

        return true;
    }

    // GetAccessControl follows links, so it reports the target's ACL. The link itself is a separate entry in its parent
    // that other accounts may be able to delete and recreate, and the registry would keep pointing through it.
    // Deliberately broad (any reparse point, OneDrive folders too): an install folder has no reason to be one.
    private static bool IsLink(DirectoryInfo directory) => (directory.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// The parent walk and the ACLs only mean something for the folder's real location on a local fixed disk. A subst
    /// drive hides its real parents (and the letter can later belong to a USB stick); a network path is governed by the
    /// server's accounts and share permissions, which this check cannot see.
    /// </summary>
    private static bool IsOnLocalFixedDriveAsWritten(string fullPath)
    {
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || Path.GetPathRoot(fullPath) is not { Length: > 0 } root
            || GetDriveTypeW(root) != DriveFixed)
            return false;

        using var handle = CreateFileW(@"\\?\" + fullPath, FileReadAttributes, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
            return false;

        // FAT and exFAT keep no ACLs: anyone can replace files there, whatever .NET reports for the folder.
        if (!GetVolumeInformationByHandleW(handle, 0, 0, 0, 0, out var volumeFlags, 0, 0) || (volumeFlags & FilePersistentAcls) == 0)
            return false;
        var buffer = new char[short.MaxValue];
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            return false;

        var real = new string(buffer, 4, (int)length - 4); // drop \\?\
        return string.Equals(real.TrimEnd('\\'), InputPath.ToLongPath(fullPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string root);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformationByHandleW(
        SafeFileHandle file, nint volumeName, uint volumeNameSize, nint serialNumber, nint maxComponentLength,
        out uint fileSystemFlags, nint fileSystemName, uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    /// <param name="countInheritOnly">
    /// For the install folder and the folders in it: entries that apply only to files and folders created later count
    /// too, because setup checks the folder before it copies anything into it.
    /// </param>
    private static bool IsSafe(FileSystemSecurity acl, FileSystemRights dangerous, HashSet<SecurityIdentifier> trusted, bool countInheritOnly)
    {
        // The owner can always rewrite the DACL.
        if (acl.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !trusted.Contains(owner))
            return false;

        foreach (FileSystemAccessRule rule in acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if (!countInheritOnly && rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                continue; // does not apply to this folder; what it passes down is checked where it lands
            if ((rule.FileSystemRights & dangerous) != 0 && !trusted.Contains((SecurityIdentifier)rule.IdentityReference))
                return false;
        }

        return true;
    }
}
