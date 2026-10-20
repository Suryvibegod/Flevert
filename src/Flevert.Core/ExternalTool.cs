using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core;

/// <summary>What a tool run by <see cref="ExternalTool"/> left behind.</summary>
/// <param name="StandardOutputTruncated">More than <see cref="ExternalTool.MaxCapturedBytes"/> was written; the rest was discarded.</param>
public sealed record ExternalToolResult(
    int ExitCode, string StandardOutput, string StandardError, bool StandardOutputTruncated, bool StandardErrorTruncated)
{
    /// <summary>
    /// The tool asked for more memory than <see cref="ExternalToolLimits.MaxMemoryBytes"/> and was refused. It may have
    /// crashed, failed, or carried on without that memory, so its exit code and output cannot be trusted, success
    /// included.
    /// </summary>
    public bool MemoryLimitExceeded { get; init; }
}

/// <summary>What the job of a run lets the tool do; nothing is limited by default.</summary>
public sealed record ExternalToolLimits
{
    /// <summary>The most memory the tool may commit. Past it an allocation fails, and the result says so.</summary>
    public ulong? MaxMemoryBytes { get; init; }

    /// <summary>
    /// The tool cannot start other processes: Windows refuses the start. Only for tools that never need to, such as qpdf.
    /// </summary>
    public bool SingleProcess { get; init; }
}

/// <summary>
/// Runs a separate program (qpdf) so that whatever it does, Flevert stays in control: no shell ever sees the arguments,
/// secrets travel through standard input, output cannot exhaust memory, and the program and everything it starts end with
/// the call, on a timeout, on cancellation, or when flevert.exe itself dies.
/// </summary>
public static partial class ExternalTool
{
    /// <summary>How much of each of stdout and stderr is kept; the rest is read and discarded so the tool never blocks.</summary>
    public const int MaxCapturedBytes = 1024 * 1024;

    // Bounds the wait for output after a kill: only a process that escaped the job (see AssignToJob) can still hold the
    // pipes then, and the caller is not kept waiting for it.
    private static readonly TimeSpan DrainAfterKill = TimeSpan.FromSeconds(1);

    private const int JobObjectAssociateCompletionPortInformationClass = 7;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    // A crashing tool ends at once instead of waiting on a Windows Error Reporting dialog until the timeout.
    private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    // winnt.h: posted when a process in the job is refused memory past its limit.
    private const uint JobObjectMsgProcessMemoryLimit = 9;
    private const nint InvalidHandleValue = -1;

    /// <summary>
    /// Starts <paramref name="toolPath"/> with <paramref name="arguments"/>, writes <paramref name="standardInput"/> to it
    /// and closes its input, and returns once it has exited. Anything it started and left running is ended then too.
    /// </summary>
    /// <param name="toolPath">Absolute path of an existing <c>.exe</c>. Scripts are refused: Windows runs them through cmd.</param>
    /// <param name="arguments">Passed one by one (<see cref="ProcessStartInfo.ArgumentList"/>), never joined into a command.</param>
    /// <param name="timeout">After this, the tool and its children are killed and <see cref="TimeoutException"/> is thrown.</param>
    /// <param name="standardInput">Bytes for the tool's stdin, such as a password; not kept after the call.</param>
    /// <param name="workingDirectory">Absolute, existing folder; the tool's own folder when <c>null</c>.</param>
    /// <param name="limits">Memory and process limits for the tool's job; none when <c>null</c>.</param>
    /// <exception cref="OperationCanceledException">Cancelled; the tool and its children were killed first.</exception>
    public static Task<ExternalToolResult> RunAsync(
        string toolPath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        ReadOnlyMemory<byte> standardInput = default,
        string? workingDirectory = null,
        ExternalToolLimits? limits = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(toolPath, arguments, timeout, null, standardInput, workingDirectory, limits, cancellationToken);

    /// <param name="afterStart">For tests: runs after the tool has started and before it is put in its job.</param>
    internal static async Task<ExternalToolResult> RunAsync(
        string toolPath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        Action<Process>? afterStart,
        ReadOnlyMemory<byte> standardInput = default,
        string? workingDirectory = null,
        ExternalToolLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        CheckToolPath(toolPath);
        workingDirectory = CheckWorkingDirectory(workingDirectory) ?? Path.GetDirectoryName(toolPath)!;
        cancellationToken.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(toolPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true, // always: otherwise the tool would share flevert's own stdin
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), // never a BOM before the input
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var memoryReports = limits?.MaxMemoryBytes is null ? null : CreateCompletionPort();
        using var job = CreateJob(limits, memoryReports);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{toolPath} did not start.");
        afterStart?.Invoke(process);
        try
        {
            AssignToJob(job, process);
        }
        catch (Win32Exception) when (process.HasExited)
        {
            // A fast tool can finish before it is put in the job, and Windows then refuses with "Access is denied". It
            // has nothing left to limit or kill; whatever it started meanwhile is the gap described at AssignToJob.
        }
        catch (Win32Exception)
        {
            // Without the job, a crash of flevert.exe could leave the tool running: do not run it at all.
            Kill(process, job);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // Process.Dispose leaves a redirected stream open once it has been read (the caller could still hold it), so the
        // pipes are closed here when the run ends; a read still waiting on a tool that escaped the job then stops too.
        using var outputPipe = process.StandardOutput;
        using var errorPipe = process.StandardError;

        // Started together: a tool that fills one pipe while nobody reads it would stop and never exit.
        var output = ReadCappedAsync(outputPipe.BaseStream);
        var error = ReadCappedAsync(errorPipe.BaseStream);
        var input = WriteInputAsync(process.StandardInput, standardInput);

        using var timer = new CancellationTokenSource(timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            // A child it started may still hold the pipes open; it is part of this run and ends with it.
            _ = TerminateJobObject(job, 1);
            var (stdout, stdoutTruncated) = await output.WaitAsync(stop.Token).ConfigureAwait(false);
            var (stderr, stderrTruncated) = await error.WaitAsync(stop.Token).ConfigureAwait(false);
            await input.WaitAsync(stop.Token).ConfigureAwait(false);
            return new ExternalToolResult(process.ExitCode, stdout, stderr, stdoutTruncated, stderrTruncated)
            {
                MemoryLimitExceeded = ReportsMemoryLimit(memoryReports),
            };
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            Kill(process, job);
            // Never return while the tool runs: the caller may delete its files next.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAny(Task.WhenAll(output, error, input), Task.Delay(DrainAfterKill, CancellationToken.None))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(toolPath)} did not finish within {timeout.TotalSeconds:0.#} s.");
        }
    }

    private static void CheckToolPath(string toolPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPath);
        if (!Path.IsPathFullyQualified(toolPath))
            throw new ArgumentException($"The tool path must be absolute: {toolPath}", nameof(toolPath));
        // CreateProcess runs .bat and .cmd files through cmd.exe, which would parse the arguments as a command line.
        if (!string.Equals(Path.GetExtension(toolPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Only .exe tools are run: {toolPath}", nameof(toolPath));
        if (!File.Exists(toolPath))
            throw new FileNotFoundException("The tool was not found.", toolPath);
    }

    private static string? CheckWorkingDirectory(string? workingDirectory)
    {
        if (workingDirectory is null)
            return null;
        if (!Path.IsPathFullyQualified(workingDirectory))
            throw new ArgumentException($"The working folder must be absolute: {workingDirectory}", nameof(workingDirectory));
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"The working folder was not found: {workingDirectory}");
        return workingDirectory;
    }

    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(Stream stream)
    {
        var kept = new MemoryStream();
        var truncated = false;
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                var room = MaxCapturedBytes - (int)kept.Length;
                if (read > room)
                    truncated = true;
                kept.Write(buffer, 0, Math.Min(read, room));
            }
        }
        catch (IOException)
        {
            // The pipe broke because the tool was killed: what was read so far is all there is.
        }
        catch (ObjectDisposedException)
        {
        }

        return (Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length), truncated);
    }

    private static async Task WriteInputAsync(StreamWriter stdin, ReadOnlyMemory<byte> bytes)
    {
        try
        {
            // Through the raw pipe, so the bytes are neither re-encoded nor copied into a buffer the writer keeps.
            if (!bytes.IsEmpty)
                await stdin.BaseStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The tool exited without reading all of its input; its exit code says whether that was a failure.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            // Closing tells the tool its input has ended; Process.Dispose would not close it, since it was used.
            CloseQuietly(stdin);
        }
    }

    // The writer holds nothing (the bytes went to its stream), so closing only closes the pipe; a broken one may still
    // complain.
    private static void CloseQuietly(StreamWriter stdin)
    {
        try
        {
            stdin.Dispose();
        }
        catch (IOException)
        {
        }
    }

    private static void Kill(Process process, SafeHandle job)
    {
        // The job holds the tool and everything it started after AssignToJob; the tree kill also reaches a child started
        // in the moment before that, while its parent is still alive to be found.
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            // Already exited, or a child could not be opened: terminating the job below still covers it.
        }

        _ = TerminateJobObject(job, 1);
    }

    /// <summary>
    /// A job whose processes all die when its last handle closes, which Windows does when flevert.exe exits or crashes.
    /// The handle is not inheritable, so the tool cannot keep its own job alive.
    /// </summary>
    internal static SafeHandle CreateKillOnCloseJob() => CreateJob(limits: null, memoryReports: null);

    /// <summary>
    /// <see cref="CreateKillOnCloseJob"/> with <paramref name="limits"/> on top. Windows reports a refused allocation
    /// only as a message to a completion port associated with the job, so <paramref name="memoryReports"/> is required
    /// with a memory limit.
    /// </summary>
    private static SafeKernelHandle CreateJob(ExternalToolLimits? limits, SafeHandle? memoryReports)
    {
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        try
        {
            if (memoryReports is not null)
            {
                // Associated before any process joins, as the documentation advises, so no message is missed.
                var port = new JobObjectAssociateCompletionPort { CompletionPort = memoryReports.DangerousGetHandle() };
                if (!SetInformationJobObject(job, JobObjectAssociateCompletionPortInformationClass, ref port,
                        (uint)Unsafe.SizeOf<JobObjectAssociateCompletionPort>()))
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitDieOnUnhandledException,
                },
            };
            if (limits?.MaxMemoryBytes is { } maxMemory)
            {
                ArgumentOutOfRangeException.ThrowIfZero(maxMemory, nameof(limits));
                info.BasicLimitInformation.LimitFlags |= JobObjectLimitProcessMemory;
                info.ProcessMemoryLimit = checked((nuint)maxMemory);
            }

            if (limits?.SingleProcess == true)
            {
                // The console host Windows starts for the tool joins its job too, and is not refused (checked with qpdf
                // 200 times, 2026-10-20).
                info.BasicLimitInformation.LimitFlags |= JobObjectLimitActiveProcess;
                info.BasicLimitInformation.ActiveProcessLimit = 1;
            }

            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info,
                    (uint)Unsafe.SizeOf<JobObjectExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch
        {
            job.Dispose();
            throw;
        }

        return job;
    }

    private static SafeKernelHandle CreateCompletionPort()
    {
        var port = CreateIoCompletionPort(InvalidHandleValue, 0, 0, 1);
        return port.IsInvalid ? throw new Win32Exception(Marshal.GetLastPInvokeError()) : port;
    }

    /// <summary>
    /// Whether the job reported a refused allocation. Windows sends the report when the allocation fails, while the tool
    /// still runs, so once it has exited the report is already queued. The documentation calls these reports best effort
    /// ("delivery to the completion port is not guaranteed"); the port of a job holds a handful of messages at most.
    /// </summary>
    private static bool ReportsMemoryLimit(SafeHandle? memoryReports)
    {
        if (memoryReports is null)
            return false;
        var exceeded = false;
        while (GetQueuedCompletionStatus(memoryReports, out var message, out _, out _, 0))
            exceeded |= message == JobObjectMsgProcessMemoryLimit;
        return exceeded;
    }

    /// <summary>
    /// Puts a started process in the job; whatever it starts from then on is in the job too. <see cref="Process"/> cannot
    /// start a process suspended, so a child the tool starts in the instant before this call would not be: qpdf starts
    /// none, and a cancel or timeout still kills the tree by parent. Starting suspended would mean calling CreateProcess
    /// directly, with its pipes and quoting, for a gap the tools Flevert runs do not use.
    /// </summary>
    internal static void AssignToJob(SafeHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.SafeHandle))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectAssociateCompletionPort
    {
        public nint CompletionKey;
        public nint CompletionPort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary>A job or a completion port.</summary>
    private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeKernelHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeKernelHandle CreateJobObjectW(nint securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeHandle job, int infoClass, ref JobObjectAssociateCompletionPort info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeKernelHandle CreateIoCompletionPort(nint fileHandle, nint existingPort, nuint key, uint threads);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetQueuedCompletionStatus(
        SafeHandle port, out uint message, out nuint key, out nint overlapped, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeHandle job, SafeHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
