using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Pdf;

/// <summary>
/// Which file a handle is open on: its volume's serial number and its 128-bit file ID (<c>FILE_ID_INFO</c>), the same
/// through every name, hard link and handle of the file. The PDF worker reopens the input by path and checks it is the
/// file its parent holds (ADR-0008, "Reading untrusted PDFs in a worker").
/// </summary>
internal readonly partial record struct FileIdentity(ulong Volume, UInt128 File)
{
    private const int FileIdInfoClass = 18;

    /// <remarks>
    /// A file system without 128-bit IDs (FAT, some network shares) is read through <c>GetFileInformationByHandle</c>
    /// instead, whose 64-bit index the runner's pin already relies on there; parent and worker ask the same file system, so
    /// they read the same form.
    /// </remarks>
    public static FileIdentity Of(SafeFileHandle handle)
    {
        // Linux: the device and inode numbers, the same through every name and handle of the file.
        if (OperatingSystem.IsLinux())
        {
            return Flevert.Core.Libc.TryStat(handle, out var stat)
                ? new FileIdentity(((ulong)stat.DevMajor << 32) | stat.DevMinor, stat.Ino)
                : throw new IOException(Flevert.Core.Libc.ErrorMessage(Marshal.GetLastPInvokeError()));
        }

        if (GetFileInformationByHandleEx(handle, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
            return new FileIdentity(info.VolumeSerialNumber, new UInt128(info.FileIdHigh, info.FileIdLow));
        if (!GetFileInformationByHandle(handle, out var basic))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new FileIdentity(basic.VolumeSerialNumber, ((ulong)basic.FileIndexHigh << 32) | basic.FileIndexLow);
    }

    /// <summary>"&lt;volume&gt;:&lt;file&gt;" in hexadecimal, as the request to the worker carries it.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Volume:x}:{File:x}");

    /// <exception cref="FormatException">Not the form <see cref="ToString"/> writes.</exception>
    public static FileIdentity Parse(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 2
            || !ulong.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var volume)
            || !UInt128.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var file))
            throw new FormatException($"Not a file identity: {text}");
        return new FileIdentity(volume, file);
    }

    // FILE_ID_INFO: the serial number, then the 16 bytes of FILE_ID_128 in memory order (little-endian halves on x64).
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    // FILETIME is two DWORDs: 4-byte packing keeps the native layout (no padding before the times).
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileIdInfo info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}
