using System.Security.AccessControl;
using System.Security.Principal;

namespace ConvertAnything.Core.Menu;

public static class InstallLocation
{
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
        if (IsLink(root) || !IsSafe(root.GetAccessControl(), WriteRights, trusted))
            return false;

        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            FileSystemSecurity acl = entry is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)entry).GetAccessControl();
            if (!IsSafe(acl, WriteRights, trusted))
                return false;
        }

        for (var parent = root.Parent; parent is not null; parent = parent.Parent)
        {
            if (stopAt is not null && string.Equals(parent.FullName.TrimEnd('\\'), stopAt.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                break;
            if (IsLink(parent) || !IsSafe(parent.GetAccessControl(), ParentRights, trusted))
                return false;
        }

        return true;
    }

    // GetAccessControl follows links, so it reports the target's ACL. The link itself is a separate entry in its parent
    // that other accounts may be able to delete and recreate, and the registry would keep pointing through it.
    private static bool IsLink(DirectoryInfo directory) => (directory.Attributes & FileAttributes.ReparsePoint) != 0;

    private static bool IsSafe(FileSystemSecurity acl, FileSystemRights dangerous, HashSet<SecurityIdentifier> trusted)
    {
        // The owner can always rewrite the DACL.
        if (acl.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !trusted.Contains(owner))
            return false;

        foreach (FileSystemAccessRule rule in acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                continue; // inherit-only entries do not apply to this object; children are checked on their own
            if ((rule.FileSystemRights & dangerous) != 0 && !trusted.Contains((SecurityIdentifier)rule.IdentityReference))
                return false;
        }

        return true;
    }
}
