using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flevert.Pdf.Tests;

/// <summary>
/// Finds the processes this test process started, by parent process id rather than by name: other test classes, agents
/// and CI jobs run their own flevert.exe at the same time.
/// </summary>
internal static partial class TestProcesses
{
    private const int ProcessBasicInformation = 0;
    private const int ProcessCommandLineInformation = 60;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// The PDF workers this process started: flevert.exe children whose command line ends with <c>pdf-worker</c> (the
    /// app runs of FlevertExeTests are children named flevert too). Classes that start workers share a collection
    /// (<see cref="WorkerProcesses"/>), so at most one test's worker runs at a time.
    /// </summary>
    public static List<Process> Workers()
    {
        var children = new List<Process>();
        foreach (var process in Process.GetProcessesByName("flevert"))
        {
            try
            {
                if (ParentId(process.Id) == Environment.ProcessId && CommandLine(process).EndsWith(" pdf-worker", StringComparison.Ordinal))
                {
                    children.Add(process);
                    continue;
                }
            }
            catch (Win32Exception)
            {
                // Exited meanwhile.
            }

            process.Dispose();
        }

        return children;
    }

    /// <summary>Waits until a worker runs, or fails the test.</summary>
    public static async Task<Process> WaitForWorkerAsync(TimeSpan within)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < within)
        {
            var workers = Workers();
            if (workers.Count > 0)
            {
                Assert.True(workers.Count == 1, $"{workers.Count} PDF workers run at once");
                return workers[0];
            }

            await Task.Delay(20);
        }

        Assert.Fail($"no PDF worker started within {within}");
        return null!;
    }

    /// <summary>The command line Windows keeps for the process, as any other process of the user can read it.</summary>
    public static string CommandLine(Process process)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
        if (handle == 0)
            throw new Win32Exception();
        try
        {
            _ = NtQueryInformationProcess(handle, ProcessCommandLineInformation, 0, 0, out var size);
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out _);
                Assert.True(status == 0, $"NtQueryInformationProcess: 0x{status:X8}");
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

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, nint info, uint length, out uint returned);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int infoClass, ref BasicInformation info, uint length, out uint returned);
}
