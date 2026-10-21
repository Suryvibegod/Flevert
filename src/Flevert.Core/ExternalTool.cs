using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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

    /// <summary>
    /// Windows refused to run the tool under <see cref="ExternalToolLimits.SingleProcess"/>, so it ran once more without
    /// that limit (a console host counted against it, which Windows 11 does not do).
    /// </summary>
    public bool SingleProcessLimitDropped { get; init; }
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

    /// <summary>
    /// The longest line handed to a <c>standardOutputLine</c> callback; the rest of a longer line is dropped, so a tool
    /// that never writes a line break cannot make Flevert hold an unbounded line.
    /// </summary>
    public const int MaxLineChars = 64 * 1024;

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
    /// <param name="environment">
    /// Changes to the environment the tool inherits from flevert.exe: a value sets the variable, <c>null</c> removes it.
    /// Never for secrets: other processes of the user can read a process's environment.
    /// </param>
    /// <param name="standardOutputLine">
    /// Called with each line the tool writes to standard output as soon as it is written, read as UTF-8, without its
    /// <c>\n</c> or <c>\r\n</c>, and cut to <see cref="MaxLineChars"/>; a last line without a break counts too. Called on
    /// one thread at a time, in order, before this returns; the lines are still in
    /// <see cref="ExternalToolResult.StandardOutput"/>. If it throws, the tool is killed and the exception is rethrown.
    /// </param>
    /// <exception cref="OperationCanceledException">Cancelled; the tool and its children were killed first.</exception>
    public static Task<ExternalToolResult> RunAsync(
        string toolPath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        ReadOnlyMemory<byte> standardInput = default,
        string? workingDirectory = null,
        ExternalToolLimits? limits = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? standardOutputLine = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(toolPath, arguments, timeout, null, standardInput, workingDirectory, limits, environment, null, standardOutputLine,
            cancellationToken);

    /// <param name="afterStart">For tests: runs after the tool has started and before it is put in its job.</param>
    /// <param name="assign">For tests: puts the tool in its job instead of <see cref="AssignToJob"/>.</param>
    internal static async Task<ExternalToolResult> RunAsync(
        string toolPath,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        Action<Process>? afterStart,
        ReadOnlyMemory<byte> standardInput = default,
        string? workingDirectory = null,
        ExternalToolLimits? limits = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<SafeHandle, Process>? assign = null,
        Action<string>? standardOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        CheckToolPath(toolPath);
        workingDirectory = CheckWorkingDirectory(workingDirectory) ?? Path.GetDirectoryName(toolPath)!;
        cancellationToken.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(OperatingSystem.IsLinux() ? UnixGroup.Setsid : toolPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true, // always: otherwise the tool would share flevert's own stdin
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), // never a BOM before the input
        };
        if (OperatingSystem.IsLinux())
        {
            foreach (var argument in UnixGroup.Prefix(toolPath, limits))
                start.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        ChangeEnvironment(start, environment);

        var run = new Run(start, timeout, standardInput, afterStart, assign ?? AssignToJob, standardOutputLine);
        try
        {
            return await run.OnceAsync(limits, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessLimitRefusedException e)
        {
            // Windows 11 lets the console host it starts for the tool join the job without counting; if a version counts
            // it, the tool cannot run under the limit at all, and running it without the limit beats not running it.
            Trace.TraceWarning($"{Path.GetFileName(toolPath)}: Windows refused the one-process limit ({e.InnerException?.Message}); running it without.");
            var result = await run.OnceAsync(limits! with { SingleProcess = false }, cancellationToken).ConfigureAwait(false);
            return result with { SingleProcessLimitDropped = true };
        }
    }

    private const int ErrorAccessDenied = 5;
    private const int ErrorNotEnoughQuota = 1816;

    /// <summary>Windows refused the tool under the one-process limit and ended it; it can run again without.</summary>
    private sealed class ProcessLimitRefusedException(Exception inner) : Exception(inner.Message, inner);

    /// <summary>One start of the tool, in a job of its own, with its output read and its input written.</summary>
    private sealed class Run(
        ProcessStartInfo start, TimeSpan timeout, ReadOnlyMemory<byte> standardInput, Action<Process>? afterStart,
        Action<SafeHandle, Process> assign, Action<string>? standardOutputLine)
    {
        public async Task<ExternalToolResult> OnceAsync(ExternalToolLimits? limits, CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsLinux())
            {
                // No job: the tool leads a process group of its own (setsid), which is ended as the job would be.
                using var tool = UnixStarter.Start(start);
                afterStart?.Invoke(tool);
                using var memory = limits?.MaxMemoryBytes is null ? null : new UnixMemoryWatch(tool.Id);
                return await WaitAsync(tool, job: null, memoryReports: null, limits, cancellationToken, memory).ConfigureAwait(false);
            }

            using var memoryReports = limits?.MaxMemoryBytes is null ? null : CreateCompletionPort();
            using var job = CreateJob(limits, memoryReports);
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"{start.FileName} did not start.");
            try
            {
                afterStart?.Invoke(process);
                assign(job, process);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorAccessDenied && process.HasExited)
            {
                // A fast tool can finish before it is put in the job, and Windows then refuses with "Access is denied". It
                // has nothing left to limit or kill; whatever it started meanwhile is the gap described at AssignToJob.
            }
            catch (Exception e)
            {
                // Without the job, a crash of flevert.exe could leave the tool running: do not run it at all.
                Kill(process, job);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                if (e is Win32Exception { NativeErrorCode: ErrorNotEnoughQuota } && limits?.SingleProcess == true)
                    throw new ProcessLimitRefusedException(e);
                throw;
            }

            return await WaitAsync(process, job, memoryReports, limits, cancellationToken).ConfigureAwait(false);
        }

        /// <param name="job">The tool's job on Windows; <c>null</c> on Linux, where its process group stands in for it.</param>
        /// <param name="memory">On Linux, how much memory the tool took, when it runs under a limit.</param>
        private async Task<ExternalToolResult> WaitAsync(
            Process process, SafeHandle? job, SafeHandle? memoryReports, ExternalToolLimits? limits, CancellationToken cancellationToken,
            UnixMemoryWatch? memory = null)
        {
            // Process.Dispose leaves a redirected stream open once it has been read (the caller could still hold it), so
            // the pipes are closed here when the run ends. A read already blocked on a process that escaped the job is not
            // interrupted by that (the pipes are synchronous); the wait for it after a kill is bounded by DrainAfterKill.
            using var outputPipe = process.StandardOutput;
            using var errorPipe = process.StandardError;

            // A callback that throws ends the run: the tool is killed as on a cancel, and the exception is rethrown.
            using var callbackFailed = new CancellationTokenSource();
            var lines = standardOutputLine is null ? null : new LineSplitter(standardOutputLine, callbackFailed);

            // Started together: a tool that fills one pipe while nobody reads it would stop and never exit.
            var output = ReadCappedAsync(outputPipe.BaseStream, lines);
            var error = ReadCappedAsync(errorPipe.BaseStream, null);
            var input = WriteInputAsync(process.StandardInput, standardInput);

            using var timer = new CancellationTokenSource(timeout);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token, callbackFailed.Token);
            try
            {
                await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
                if (OperatingSystem.IsLinux())
                    memory?.Dispose();
                // A child it started may still hold the pipes open; it is part of this run and ends with it.
                EndWhatIsLeft(process, job);
                var (stdout, stdoutTruncated) = await output.WaitAsync(stop.Token).ConfigureAwait(false);
                var (stderr, stderrTruncated) = await error.WaitAsync(stop.Token).ConfigureAwait(false);
                await input.WaitAsync(stop.Token).ConfigureAwait(false);
                lines?.ThrowIfFailed();
                return new ExternalToolResult(process.ExitCode, stdout, stderr, stdoutTruncated, stderrTruncated)
                {
                    MemoryLimitExceeded = OperatingSystem.IsLinux()
                        ? limits?.MaxMemoryBytes is { } limit && process.ExitCode != 0
                            && (UnixGroup.LooksOutOfMemory(stderr)
                                || (memory is not null && UnixGroup.DiedNearItsLimit(process.ExitCode, memory.PeakBytes, limit)))
                        : ReportsMemoryLimit(memoryReports),
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
                lines?.ThrowIfFailed();
                throw new TimeoutException($"{Path.GetFileName(start.FileName)} did not finish within {timeout.TotalSeconds:0.#} s.");
            }
        }
    }

    private static void CheckToolPath(string toolPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPath);
        if (!Path.IsPathFullyQualified(toolPath))
            throw new ArgumentException($"The tool path must be absolute: {toolPath}", nameof(toolPath));
        // CreateProcess runs .bat and .cmd files through cmd.exe, which would parse the arguments as a command line. Linux
        // passes the arguments to whatever is started as they are.
        if (OperatingSystem.IsWindows() && !string.Equals(Path.GetExtension(toolPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Only .exe tools are run: {toolPath}", nameof(toolPath));
        if (!File.Exists(toolPath))
            throw new FileNotFoundException("The tool was not found.", toolPath);
    }

    // ProcessStartInfo starts from flevert's own environment; on Windows its names compare case-insensitively.
    private static void ChangeEnvironment(ProcessStartInfo start, IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null)
            return;
        foreach (var (name, value) in environment)
        {
            // "=" ends a name in the environment block and "\0" ends an entry: either would set a different variable.
            if (name.Length == 0 || name.AsSpan().ContainsAny('=', '\0'))
                throw new ArgumentException($"Not a variable name an environment can hold: \"{name}\".", nameof(environment));
            if (value is not null && value.Contains('\0', StringComparison.Ordinal))
                throw new ArgumentException($"The value of {name} holds a null character, which would end it.", nameof(environment));
            if (value is null)
                start.Environment.Remove(name);
            else
                start.Environment[name] = value;
        }
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

    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(Stream stream, LineSplitter? lines)
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
                lines?.Add(buffer.AsSpan(0, read));
            }

            lines?.End();
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

    /// <summary>
    /// Turns the bytes of standard output into lines for a callback as they arrive. Once the callback has thrown it is not
    /// called again; the run is stopped through <c>failed</c>, and <see cref="ThrowIfFailed"/> rethrows the exception.
    /// </summary>
    private sealed class LineSplitter(Action<string> onLine, CancellationTokenSource failed)
    {
        private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();
        private readonly StringBuilder _line = new();
        private char[] _chars = new char[1024];
        private bool _pending;
        private ExceptionDispatchInfo? _error;

        public void Add(ReadOnlySpan<byte> bytes) => Decode(bytes, flush: false);

        /// <summary>The output has ended: a last line without a break is a line too.</summary>
        public void End()
        {
            Decode([], flush: true);
            if (_pending)
                Emit();
        }

        public void ThrowIfFailed() => _error?.Throw();

        private void Decode(ReadOnlySpan<byte> bytes, bool flush)
        {
            var needed = _decoder.GetCharCount(bytes, flush);
            if (needed > _chars.Length)
                _chars = new char[needed];
            var count = _decoder.GetChars(bytes, _chars, flush);
            foreach (var c in _chars.AsSpan(0, count))
            {
                if (c == '\n')
                {
                    Emit();
                    continue;
                }

                _pending = true;
                // One character past the cap is kept, so a "\r" right after a line of exactly the cap is still removed;
                // the rest of a longer line is dropped.
                if (_line.Length <= MaxLineChars)
                    _line.Append(c);
            }
        }

        private void Emit()
        {
            var length = _line.Length;
            if (length > 0 && _line[length - 1] == '\r')
                length--;
            var line = _line.ToString(0, Math.Min(length, MaxLineChars));
            _line.Clear();
            _pending = false;
            if (_error is not null)
                return;
            try
            {
                onLine(line);
            }
#pragma warning disable CA1031 // Rethrown to the caller once the tool is killed.
            catch (Exception e)
#pragma warning restore CA1031
            {
                _error = ExceptionDispatchInfo.Capture(e);
                failed.Cancel();
            }
        }
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

    private static void Kill(Process process, SafeHandle? job)
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

        EndWhatIsLeft(process, job);
    }

    // The job on Windows, the tool's process group on Linux: whatever the tool started and left running.
    private static void EndWhatIsLeft(Process process, SafeHandle? job)
    {
        if (OperatingSystem.IsLinux())
            UnixGroup.Kill(process.Id);
        else if (job is not null)
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
        Debug.Assert(limits?.MaxMemoryBytes is null || memoryReports is not null, "a memory limit needs a port for its reports");
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
