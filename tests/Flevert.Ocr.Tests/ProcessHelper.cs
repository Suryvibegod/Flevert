using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flevert.Ocr.Tests;

internal static partial class ProcessHelper
{
    private const int ProcessBasicInformation = 0;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x102;

    public static int? FindChildTesseractProcessId(int parentPid)
    {
        var candidates = Process.GetProcessesByName("tesseract");
        try
        {
            foreach (var process in candidates)
            {
                if (GetParentProcessId(process.Id) == parentPid && IsProcessRunning(process.Id))
                    return process.Id;
            }

            return null;
        }
        finally
        {
            foreach (var process in candidates)
                process.Dispose();
        }
    }

    public static bool IsProcessRunning(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)processId);
        if (handle == 0)
            return false;
        try
        {
            return WaitForSingleObject(handle, 0) == WaitTimeout;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static int? GetParentProcessId(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == 0)
            return null;
        try
        {
            var info = new BasicInformation();
            return NtQueryInformationProcess(handle, ProcessBasicInformation, ref info, (uint)Marshal.SizeOf<BasicInformation>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, ref BasicInformation info, uint length, out uint returned);
}
