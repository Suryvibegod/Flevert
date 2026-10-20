using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flevert.Pdf.Tests;

internal static partial class TestProcesses
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    // x64 offsets: PEB.ProcessParameters, then RTL_USER_PROCESS_PARAMETERS.Environment and .EnvironmentSize.
    private const int ProcessParametersOffset = 0x20;
    private const int EnvironmentOffset = 0x80;
    private const int EnvironmentSizeOffset = 0x3F0;

    /// <summary>
    /// The names of the environment variables <paramref name="process"/> holds now, read from its process parameters as
    /// any process of the same user can read them.
    /// </summary>
    public static HashSet<string> EnvironmentNames(Process process)
    {
        Assert.True(Environment.Is64BitProcess, "the offsets below are those of a 64-bit process");
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, (uint)process.Id);
        if (handle == 0)
            throw new Win32Exception();
        try
        {
            var info = new BasicInformation();
            var status = NtQueryInformationProcess(handle, ProcessBasicInformation, ref info, (uint)Marshal.SizeOf<BasicInformation>(), out _);
            Assert.True(status == 0, $"NtQueryInformationProcess: 0x{status:X8}");
            var parameters = ReadPointer(handle, info.PebBaseAddress + ProcessParametersOffset);
            var block = ReadPointer(handle, parameters + EnvironmentOffset);
            var size = (int)ReadPointer(handle, parameters + EnvironmentSizeOffset);
            Assert.True(size is > 0 and < 1 << 24, $"environment size {size}");
            var bytes = Read(handle, block, size);
            var text = new string(MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, size & ~1)));
            // NAME=value entries ended by U+0000, the block by an empty one; "=C:=C:\..." entries keep the current folders.
            return text.Split('\0').TakeWhile(entry => entry.Length > 0)
                .Select(entry => entry[..(entry.IndexOf('=', 1) is var at and > 0 ? at : entry.Length)])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static nint ReadPointer(nint process, nint address) => (nint)BitConverter.ToInt64(Read(process, address, IntPtr.Size));

    private static byte[] Read(nint process, nint address, int length)
    {
        var buffer = new byte[length];
        if (!ReadProcessMemory(process, address, buffer, length, out var read) || read != length)
            throw new Win32Exception();
        return buffer;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nint size, out nint read);
}
