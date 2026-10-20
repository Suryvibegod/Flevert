using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core.Tests;

/// <summary>
/// Junctions as another account with write access to a folder could make them: no admin rights or Developer Mode needed.
/// </summary>
internal static class Junction
{
    private const uint FileWriteAttributes = 0x100;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    /// <summary>A new junction at <paramref name="link"/> (<c>mklink /J</c>); false when the name is taken.</summary>
    public static bool TryCreate(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        return mklink.ExitCode == 0;
    }

    /// <summary>
    /// Turns the existing empty folder <paramref name="directory"/> into a junction to <paramref name="target"/> in place,
    /// the way <c>mklink /J</c> does after creating its folder; returns the Win32 error, 0 on success. Only needs write
    /// access to the folder: FILE_WRITE_ATTRIBUTES is not subject to sharing, so a handle that denies sharing does not stop
    /// it, while a folder that is not empty does (ERROR_DIR_NOT_EMPTY).
    /// </summary>
    public static int TryConvert(string directory, string target)
    {
        using var handle = CreateFileW(@"\\?\" + directory, FileWriteAttributes, ShareAll, 0, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, 0);
        if (handle.IsInvalid)
            return Marshal.GetLastPInvokeError();

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        // REPARSE_DATA_BUFFER, MountPointReparseBuffer: both names NUL-terminated, the print name after the substitute.
        var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
        var buffer = new byte[8 + dataLength];
        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        return DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length, 0, 0, out _, 0)
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    public static string Describe(int error) => error == 0 ? "converted" : new Win32Exception(error).Message;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, byte[] input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
}
