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
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x102;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    /// <summary>
    /// The PDF workers this process started: flevert.exe children whose command line ends with <c>pdf-worker</c> (the
    /// app runs of FlevertExeTests are children named flevert too). Classes that start workers share a collection
    /// (<see cref="WorkerProcesses"/>), so at most one test's worker runs at a time.
    /// </summary>
    public static List<Process> Workers() => Workers(Process.GetProcessesByName("flevert"));

    /// <summary>The workers among <paramref name="candidates"/>; disposes the others.</summary>
    internal static List<Process> Workers(IEnumerable<Process> candidates)
    {
        var children = new List<Process>();
        foreach (var process in candidates)
        {
            try
            {
                // A worker that is exiting can still be listed: it is no longer the worker a test is looking for.
                if (ParentId(process.Id) == Environment.ProcessId && IsRunning(process)
                    && CommandLine(process) is { } line && line.EndsWith(" pdf-worker", StringComparison.Ordinal))
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

    /// <summary>How many processes named <paramref name="name"/> this process started are still running (tesseract).</summary>
    public static int RunningChildren(string name)
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (ParentId(process.Id) == Environment.ProcessId && IsRunning(process))
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

    /// <summary>
    /// The command lines of the processes named <paramref name="name"/> this process started that still run after up to
    /// <paramref name="within"/>: empty when they all ended. A process just killed may take a moment to be gone.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ChildrenStillRunningAsync(string name, TimeSpan within)
    {
        var timer = Stopwatch.StartNew();
        while (RunningChildren(name) > 0 && timer.Elapsed < within)
            await Task.Delay(100);

        var left = new List<string>();
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (ParentId(process.Id) == Environment.ProcessId && IsRunning(process))
                        left.Add($"{process.Id}: {CommandLine(process)}");
                }
                catch (Win32Exception)
                {
                    // Exited meanwhile.
                }
            }
        }

        return left;
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
    /// <returns><c>null</c> when Windows does not give it, as for a process that is exiting (0xC000010A); like
    /// <c>ProcessFacts.CommandLine</c> in the product.</returns>
    public static string? CommandLine(Process process)
    {
        if (OperatingSystem.IsLinux())
            return ProcFs.CommandLine(process.Id);
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
        if (OperatingSystem.IsLinux())
            return ProcFs.IsRunning(process.Id);
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
        if (OperatingSystem.IsLinux())
            return ProcFs.ParentId(processId);
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
