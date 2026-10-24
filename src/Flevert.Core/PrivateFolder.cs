using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Flevert.Core;

/// <summary>
/// A folder only this user, SYSTEM and administrators can change, for files a tool writes or reads by a name anyone could
/// predict: qpdf writes through whatever is at that name, so a link another account put there would have it write
/// elsewhere, and tesseract would read a picture another account swapped in.
/// </summary>
public static partial class PrivateFolder
{
    private const int ErrorAlreadyExists = 183;

    /// <summary>
    /// Creates the folder with its permissions already in place (nothing inherited), so there is no moment when another
    /// account could add to it. Unlike <see cref="Directory.CreateDirectory(string)"/>, a folder already at
    /// <paramref name="path"/> is an error: someone else may have made it, with their own permissions.
    /// </summary>
    /// <exception cref="IOException">Something already exists at <paramref name="path"/>, or the folder cannot be created.</exception>
    public static unsafe void Create(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            CreateOnLinux(path);
            return;
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        using var me = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     me.User!,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var descriptor = security.GetSecurityDescriptorBinaryForm();
        fixed (byte* bytes = descriptor)
        {
            var attributes = new SecurityAttributes { Length = sizeof(SecurityAttributes), SecurityDescriptor = bytes };
            // The \\?\ form, so a folder deep inside a long path is created too.
            if (CreateDirectoryW(InputPin.ToExtendedPath(Path.GetFullPath(path)), &attributes))
                return;
        }

        var error = Marshal.GetLastPInvokeError();
        throw error == ErrorAlreadyExists
            ? new IOException($"Something already exists at {path}.")
            : new IOException($"Could not create the folder {path}.", new Win32Exception(error));
    }

    // mkdir with mode 0700 from the start; like CreateDirectoryW it fails on anything already there, a link included,
    // which it never follows.
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void CreateOnLinux(string path)
    {
        if (Flevert.Core.Libc.MakeDirectory(path, OwnerOnly) == 0)
            return;
        var error = Marshal.GetLastPInvokeError();
        throw error == Flevert.Core.Libc.EExist
            ? new IOException($"Something already exists at {path}.")
            : new IOException($"Could not create the folder {path}: {Flevert.Core.Libc.ErrorMessage(error)}");
    }

    private const uint OwnerOnly = 0x1C0; // 0700

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SecurityAttributes
    {
        public int Length;
        public void* SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateDirectoryW(string path, SecurityAttributes* attributes);
}
