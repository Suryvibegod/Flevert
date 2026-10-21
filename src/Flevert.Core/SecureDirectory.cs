using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Flevert.Core;

public static partial class SecureDirectory
{
    // Medium mandatory label, inherited by files and folders: lower-integrity processes cannot read, write or execute.
    private const string NoReadUpLabel = "S:(ML;OICI;NRNWNX;;;ME)";
    private const uint SddlRevision1 = 1;
    private const int SeFileObject = 1;
    private const uint LabelSecurityInformation = 0x10;

    /// <summary>
    /// Replaces inherited permissions with full control for this user, SYSTEM and Administrators only, so other accounts
    /// that can read the profile (for example sandbox groups some tools add) cannot read what is stored here.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void RestrictToOwnerAndAdmins(string directory)
    {
        using var me = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     me.User!,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(directory).SetAccessControl(acl);
    }

    /// <summary>
    /// The Linux counterpart of <see cref="RestrictToOwnerAndAdmins"/>: mode 0700, for a folder this user owns.
    /// </summary>
    /// <exception cref="IOException">The folder is a link, not a folder, or another account's.</exception>
    [SupportedOSPlatform("linux")]
    public static void RestrictToOwner(string directory)
    {
        // Opened without following a link: a link put in place of the folder must not pass its target's mode on.
        using var handle = Libc.Open(directory, Libc.OPath | Libc.ONofollow | Libc.ODirectory | Libc.OCloexec);
        if (handle.IsInvalid)
            throw new IOException($"{Libc.ErrorMessage(Marshal.GetLastPInvokeError())} ({directory})");
        if (!Libc.TryStat(handle, out var stat) || stat.Type != Libc.SIfDir || stat.Uid != Libc.EffectiveUserId)
            throw new IOException($"{directory} is not a folder of this user's own.");

        // Through the handle that was checked, so what changes is that folder whatever happened to its name meanwhile.
        File.SetUnixFileMode(Libc.HandlePath(handle), OwnerOnly);
    }

    [SupportedOSPlatform("linux")]
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Restricts and labels the folder, creating it if needed. Never throws: it runs after a leader acknowledged other
    /// processes' files, and it is defence in depth only.
    /// </summary>
    public static void TrySecure(string directory)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                Directory.CreateDirectory(directory, OwnerOnly);
                RestrictToOwner(directory);
            }
#pragma warning disable CA1031 // Defence in depth only, as on Windows.
            catch (Exception) { }
#pragma warning restore CA1031
            return;
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        try
        {
            Directory.CreateDirectory(directory);
            RestrictToOwnerAndAdmins(directory);
            TryApplyNoReadUpLabel(directory);
        }
#pragma warning disable CA1031 // SetAccessControl can also throw InvalidOperationException or NotSupportedException.
        catch (Exception) { }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Read-up is allowed by default, so a sandboxed low-integrity process of the same user could otherwise read the batch
    /// keys. Labelling the folder blocks that; it needs no extra privilege for a folder the user owns.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void TryApplyNoReadUpLabel(string directory)
    {
        try
        {
            ApplyNoReadUpLabel(directory);
        }
        catch (Win32Exception)
        {
            // Defence in depth only: the batch still works without the label.
        }
    }

    [SupportedOSPlatform("windows")]
    public static void ApplyNoReadUpLabel(string directory)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(NoReadUpLabel, SddlRevision1, out var descriptor, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (!GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            var error = SetNamedSecurityInfoW(directory, SeFileObject, LabelSecurityInformation, 0, 0, 0, sacl);
            if (error != 0)
                throw new Win32Exception(error);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out nint descriptor, nint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorSacl(
        nint descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out nint sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetNamedSecurityInfoW(
        string name, int objectType, uint securityInfo, nint owner, nint group, nint dacl, nint sacl);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
