using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flevert.Core.Tests;

/// <summary>
/// A killed tool is not gone when Windows has given it an exit code: TerminateProcess is asynchronous, and the process
/// still holds its files and memory until its handle is signalled. <see cref="Process.HasExited"/> (and so
/// <see cref="Process.WaitForExitAsync"/>) goes by the exit code, so it can say "exited" first. On loaded CI runners a
/// cancelled tesseract was still running after the OCR call and Make searchable had returned (2026-10-24). A debugger
/// holds the tool in that state here for as long as the test likes: Windows does not end a debugged process before its
/// debugger has seen it exit.
/// </summary>
public sealed partial class ExternalToolEndTests
{
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_cancelled_run_returns_only_once_windows_has_ended_the_tool()
    {
        using var held = new ExitHolder();
        using var cancel = new CancellationTokenSource();
        var run = ExternalTool.RunAsync(Ping, ["-n", "30", "127.0.0.1"], Generous, afterStart: process => held.Attach(process.Id),
            cancellationToken: cancel.Token);

        await cancel.CancelAsync();
        Assert.True(held.ExitSeen.Wait(TimeSpan.FromSeconds(10)), "the kill never reached the tool");

        var returnedEarly = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2))) == run;
        held.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(returnedEarly, "RunAsync returned while Windows was still ending the tool");
        Assert.False(held.StillRunning(), "the tool is still running");
    }

    [Fact]
    public async Task A_timed_out_run_returns_only_once_windows_has_ended_the_tool()
    {
        using var held = new ExitHolder();
        var run = ExternalTool.RunAsync(Ping, ["-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(500),
            afterStart: process => held.Attach(process.Id));

        Assert.True(held.ExitSeen.Wait(TimeSpan.FromSeconds(10)), "the kill never reached the tool");

        var returnedEarly = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2))) == run;
        held.Release();

        await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.False(returnedEarly, "RunAsync returned while Windows was still ending the tool");
        Assert.False(held.StillRunning(), "the tool is still running");
    }

    /// <summary>
    /// Debugs one process on a thread of its own (debug events go to the thread that attached) and, when the process
    /// exits, keeps it from ending until <see cref="Release"/>.
    /// </summary>
    private sealed class ExitHolder : IDisposable
    {
        private const uint ExceptionEvent = 1;
        private const uint CreateProcessEvent = 3;
        private const uint ExitProcessEvent = 5;
        private const uint LoadDllEvent = 6;
        private const uint DbgContinue = 0x00010002;
        private const uint DbgExceptionNotHandled = 0x80010001;
        private const uint Breakpoint = 0x80000003;
        private const uint Synchronize = 0x00100000;
        private const uint WaitTimeout = 0x102;

        private readonly ManualResetEventSlim _attached = new();
        private readonly ManualResetEventSlim _release = new();
        private Thread? _thread;
        private Exception? _error;
        private nint _handle;

        public ManualResetEventSlim ExitSeen { get; } = new();

        /// <summary>Attaches to <paramref name="processId"/> and returns once it is debugged.</summary>
        public void Attach(int processId)
        {
            // Opened before the process can end, so that its state can still be asked once its id is free.
            _handle = OpenProcess(Synchronize, false, (uint)processId);
            if (_handle == 0)
                throw new Win32Exception();
            _thread = new Thread(() => Debug((uint)processId)) { IsBackground = true, Name = "debugger of the tool" };
            _thread.Start();
            _attached.Wait();
            if (_error is not null)
                throw new InvalidOperationException("Could not debug the tool.", _error);
        }

        public void Release() => _release.Set();

        /// <summary>Whether Windows has not ended the process yet.</summary>
        public bool StillRunning() => WaitForSingleObject(_handle, 0) == WaitTimeout;

        public void Dispose()
        {
            _release.Set();
            _thread?.Join(TimeSpan.FromSeconds(10));
            if (_handle != 0)
                CloseHandle(_handle);
            _attached.Dispose();
            _release.Dispose();
            ExitSeen.Dispose();
        }

        private void Debug(uint processId)
        {
            if (!DebugActiveProcess(processId))
            {
                _error = new Win32Exception();
                _attached.Set();
                return;
            }

            // The test ending must not kill the tool: the job does that.
            DebugSetProcessKillOnExit(false);
            _attached.Set();
            while (WaitForDebugEvent(out var debugEvent, uint.MaxValue))
            {
                var status = DbgContinue;
                switch (debugEvent.Code)
                {
                    case CreateProcessEvent or LoadDllEvent when debugEvent.File != 0:
                        CloseHandle(debugEvent.File);
                        break;
                    case ExceptionEvent when debugEvent.ExceptionCode != Breakpoint:
                        status = DbgExceptionNotHandled;
                        break;
                    case ExitProcessEvent:
                        ExitSeen.Set();
                        _release.Wait();
                        ContinueDebugEvent(debugEvent.ProcessId, debugEvent.ThreadId, DbgContinue);
                        return;
                }

                ContinueDebugEvent(debugEvent.ProcessId, debugEvent.ThreadId, status);
            }
        }
    }

    /// <summary>
    /// DEBUG_EVENT (176 bytes on x64): its code, process and thread, then a union whose first field is a file handle for
    /// CREATE_PROCESS_DEBUG_INFO and LOAD_DLL_DEBUG_INFO, and the exception code for EXCEPTION_DEBUG_INFO.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 176)]
    private struct DebugEvent
    {
        [FieldOffset(0)] public uint Code;
        [FieldOffset(4)] public uint ProcessId;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(16)] public nint File;
        [FieldOffset(16)] public uint ExceptionCode;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DebugActiveProcess(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DebugSetProcessKillOnExit([MarshalAs(UnmanagedType.Bool)] bool killOnExit);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitForDebugEvent(out DebugEvent debugEvent, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ContinueDebugEvent(uint processId, uint threadId, uint status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
}
