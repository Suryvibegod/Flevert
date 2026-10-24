using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flevert.Image.Tests;

/// <summary>
/// Finds the processes this test process started, by parent process id rather than by name: other test projects, agents
/// and CI jobs run their own tesseract.exe at the same time. A minimal copy of <c>TestProcesses</c> in Flevert.Pdf.Tests.
/// </summary>
internal static partial class ChildProcesses
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x102;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>
    /// How many processes named <paramref name="name"/> this process started are still running whose command line
    /// contains <paramref name="commandLinePart"/>: other test classes in this process run tesseract at the same time.
    /// </summary>
    public static int Running(string name, string commandLinePart)
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (ParentId(process.Id) == Environment.ProcessId && IsRunning(process)
                        && CommandLine(process) is { } line && line.Contains(commandLinePart, StringComparison.OrdinalIgnoreCase))
                        count++;
                }
                catch (Win32Exception)
                {
                    // Exited meanwhile.
                }
            }
        }

        return count;
    }

    /// <summary>The command line Windows keeps for the process; <c>null</c> when Windows does not give it, as for a
    /// process that is exiting.</summary>
    private static string? CommandLine(Process process)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
        if (handle == 0)
            throw new Win32Exception();
        try
        {
            var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, 0, 0, out var size);
            if (status != StatusInfoLengthMismatch || size == 0)
                return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out _) != 0)
                    return null;
                // A UNICODE_STRING: length in bytes, maximum length, then a pointer to the characters.
                var length = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return Marshal.PtrToStringUni(text, length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Whether the process has not exited. Its handle is signalled once it has; a handle still open elsewhere
    /// keeps an exited process listed.</summary>
    private static bool IsRunning(Process process)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)process.Id);
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

    private static int? ParentId(int processId)
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
    private static partial int NtQueryInformationProcess(nint process, int infoClass, nint info, uint length, out uint returned);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, ref BasicInformation info, uint length, out uint returned);
}
