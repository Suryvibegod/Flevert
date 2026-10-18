using System.Security.AccessControl;
using System.Security.Principal;

namespace ConvertAnything.Core.Menu;

public static class InstallLocation
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// The menu runs ca.exe as whoever clicks it. If another account could replace ca.exe or plant a DLL beside it, that
    /// account could run code as this user, so registration requires a folder only this user and admins can write.
    /// </summary>
    public static bool IsWritableOnlyByOwnerAndAdmins(string directory)
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

        var rules = new DirectoryInfo(directory).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));

        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & WriteRights) != 0
                && !trusted.Contains((SecurityIdentifier)rule.IdentityReference))
                return false;
        }

        return true;
    }
}
