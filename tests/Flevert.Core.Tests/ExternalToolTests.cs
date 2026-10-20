using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Flevert.Core.Tests;

/// <summary>
/// Runs the system's own tools (cmd, findstr, ping, Windows PowerShell) the way Flevert will run qpdf. A ping left running
/// is identified by its process id or its parent's, never by counting every ping.exe on the machine: other test runs (CI
/// jobs, agents in other worktrees) start their own.
/// </summary>
public sealed partial class ExternalToolTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
    private static readonly string FindStr = Path.Combine(Environment.SystemDirectory, "findstr.exe");
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Returns_the_exit_code_and_both_outputs_separately()
    {
        var result = await ExternalTool.RunAsync(Cmd, ["/c", "echo out& echo err 1>&2& exit 7"], Generous);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out", result.StandardOutput.Trim());
        Assert.Equal("err", result.StandardError.Trim());
        Assert.False(result.StandardOutputTruncated);
        Assert.False(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task Writes_the_given_bytes_to_standard_input_and_closes_it()
    {
        // findstr echoes every line it reads and stops only at end of input: it returns only if stdin was closed. A byte
        // order mark or a re-encoding would show up in the echo.
        const string secret = "mật khẩu có dấu cách & \"quotes\"\r\n";

        var result = await ExternalTool.RunAsync(FindStr, ["^"], Generous, Encoding.UTF8.GetBytes(secret));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(secret, result.StandardOutput);
    }

    [Fact]
    public async Task A_tool_that_never_reads_its_input_does_not_hang_the_caller()
    {
        var result = await ExternalTool.RunAsync(Cmd, ["/c", "exit 3"], Generous, new byte[5 * 1024 * 1024]);

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task Keeps_at_most_one_megabyte_of_each_output_and_still_lets_the_tool_finish()
    {
        // Five megabytes on stderr first: a caller that read stdout to the end before stderr would deadlock here.
        var result = await ExternalTool.RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.Write('e' * 5MB); [Console]::Out.Write('o' * 5MB)"],
            Generous);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new string('o', ExternalTool.MaxCapturedBytes), result.StandardOutput);
        Assert.Equal(new string('e', ExternalTool.MaxCapturedBytes), result.StandardError);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
    }

    [Fact]
    public async Task A_tool_that_runs_too_long_is_killed_and_reported_as_a_timeout()
    {
        var ping = 0;
        var timer = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            ExternalTool.RunAsync(Ping, ["-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(500), afterStart: null,
                assign: (job, process) => { ping = process.Id; ExternalTool.AssignToJob(job, process); }));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2.5), $"took {timer.Elapsed}");
        Assert.NotEqual(0, ping);
        Assert.False(IsRunning(ping), "the timed-out ping is still running");
    }

    [Fact]
    public async Task Cancelling_kills_the_tool_and_what_it_started_before_returning()
    {
        var pids = _dir.Combine("pids.txt");
        var script = $"$g = Start-Process -FilePath '{Ping}' -ArgumentList '-n','30','127.0.0.1' -WindowStyle Hidden -PassThru; " +
                     $"Set-Content -LiteralPath '{pids}' -Value \"$PID $($g.Id)\"; Start-Sleep -Seconds 30";
        using var cancel = new CancellationTokenSource();

        var run = ExternalTool.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", script], Generous,
            cancellationToken: cancel.Token);
        var (child, grandchild) = await ReadPids(pids, run);
        Assert.True(IsRunning(child) && IsRunning(grandchild), "the tool or the process it started is not running");

        var timer = Stopwatch.StartNew();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(IsRunning(child), "RunAsync returned while the tool was still running");
        Assert.True(await WaitUntil(() => !IsRunning(grandchild), TimeSpan.FromSeconds(2) - timer.Elapsed),
            "the process the tool started outlived the cancellation");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"took {timer.Elapsed}");
    }

    [Fact]
    public async Task What_the_tool_leaves_running_is_ended_when_it_exits()
    {
        // cmd exits at once; the ping it started keeps cmd's output pipes open, so a caller that only waited for the
        // end of output would wait for the ping (30 s) instead of returning.
        var cmd = 0;
        var timer = Stopwatch.StartNew();

        var result = await ExternalTool.RunAsync(Cmd, ["/c", "start", "", "/b", Ping, "-n", "30", "127.0.0.1"],
            TimeSpan.FromSeconds(20), afterStart: null, assign: (job, process) => { cmd = process.Id; ExternalTool.AssignToJob(job, process); });

        Assert.Equal(0, result.ExitCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
        Assert.True(await WaitUntil(() => PingsStartedBy(cmd).Count == 0, TimeSpan.FromSeconds(2)),
            "the ping started by the tool is still running");
    }

    [Fact]
    public void Closing_the_job_kills_its_processes_as_if_flevert_had_died()
    {
        // When flevert.exe dies, Windows closes its handles, the job's last handle among them.
        var job = ExternalTool.CreateKillOnCloseJob();
        using var ping = Process.Start(new ProcessStartInfo(Ping, ["-n", "30", "127.0.0.1"])
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        try
        {
            ExternalTool.AssignToJob(job, ping);
            Assert.False(ping.HasExited);

            job.Dispose();

            Assert.True(ping.WaitForExit(TimeSpan.FromSeconds(2)), "the process outlived its job");
        }
        finally
        {
            job.Dispose();
            if (!ping.HasExited)
                ping.Kill();
        }
    }

    [Fact]
    public async Task A_tool_that_exits_before_it_joins_its_job_still_returns_its_result()
    {
        // A fast tool can finish between Process.Start and AssignProcessToJobObject, which then fails with "Access is
        // denied" (checked 2026-10-20). The seam waits for the exit, so the test never depends on that race.
        var result = await ExternalTool.RunAsync(Cmd, ["/c", "echo done& exit 7"], Generous,
            afterStart: process => process.WaitForExit());

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("done", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task The_pipes_from_the_tool_are_closed_when_the_run_returns()
    {
        // Process.Dispose leaves a redirected stream open once it has been read synchronously (the caller may still hold
        // it), so each run would leak two pipe handles until a garbage collection.
        StreamReader? stdout = null, stderr = null;

        await ExternalTool.RunAsync(Cmd, ["/c", "echo out& echo err 1>&2"], Generous,
            afterStart: process => (stdout, stderr) = (process.StandardOutput, process.StandardError));

        Assert.False(stdout!.BaseStream.CanRead, "stdout is still open");
        Assert.False(stderr!.BaseStream.CanRead, "stderr is still open");
    }

    [Fact]
    public async Task The_pipe_to_the_tool_is_closed_even_when_the_tool_exits_without_reading_it()
    {
        // Writing 5 MB to a tool that has exited fails: the pipe must still be closed, not left to the finalizer.
        StreamWriter? stdin = null;

        await ExternalTool.RunAsync(Cmd, ["/c", "exit 3"], Generous,
            afterStart: process => stdin = process.StandardInput, standardInput: new byte[5 * 1024 * 1024]);

        Assert.False(stdin!.BaseStream.CanWrite, "stdin is still open");
    }

    [Fact]
    public async Task A_tool_refused_memory_past_its_limit_is_reported_as_over_the_limit()
    {
        // Windows PowerShell (on every Windows, the CI runner included) asks for 500 MB at once. Under the limit the
        // allocation fails and the tool decides what to do about it, so only the job can tell what happened.
        string[] allocate =
            ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; $b = [byte[]]::new(500MB); $b[-1] = 1; 'allocated'"];

        var limited = await ExternalTool.RunAsync(PowerShell, allocate, Generous,
            limits: new ExternalToolLimits { MaxMemoryBytes = 200 * 1024 * 1024 });
        var free = await ExternalTool.RunAsync(PowerShell, allocate, Generous);
        var roomy = await ExternalTool.RunAsync(PowerShell, allocate, Generous,
            limits: new ExternalToolLimits { MaxMemoryBytes = 1024 * 1024 * 1024 });

        Assert.True(limited.MemoryLimitExceeded, "the run under the limit was not reported");
        // Control: a limit the tool stays under reports nothing, and the allocation succeeds.
        Assert.Equal("allocated", roomy.StandardOutput.Trim());
        Assert.False(roomy.MemoryLimitExceeded);
        Assert.DoesNotContain("allocated", limited.StandardOutput, StringComparison.Ordinal);
        // Control: the same allocation without a limit succeeds, and nothing is reported.
        Assert.Equal("allocated", free.StandardOutput.Trim());
        Assert.False(free.MemoryLimitExceeded);
    }

    [Fact]
    public async Task A_tool_limited_to_one_process_cannot_start_another()
    {
        // ping prints the address it pings in every display language.
        string[] pingOnce = ["/c", Ping, "-n", "1", "127.0.0.1"];

        var limited = await ExternalTool.RunAsync(Cmd, pingOnce, Generous, limits: new ExternalToolLimits { SingleProcess = true });
        var free = await ExternalTool.RunAsync(Cmd, pingOnce, Generous);

        Assert.NotEqual(0, limited.ExitCode);
        Assert.DoesNotContain("127.0.0.1", limited.StandardOutput, StringComparison.Ordinal);
        // Control: without the limit cmd starts ping.
        Assert.Equal(0, free.ExitCode);
        Assert.Contains("127.0.0.1", free.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tool_asked_to_run_below_normal_priority_does_and_so_does_what_it_starts()
    {
        // The pause lets the run lower the tool before it looks; the child is started after that, and inherits.
        const string Report =
            "Start-Sleep -Milliseconds 500; [Diagnostics.Process]::GetCurrentProcess().PriorityClass; " +
            "& $env:SystemRoot\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoProfile -NonInteractive -Command '[Diagnostics.Process]::GetCurrentProcess().PriorityClass'";
        string[] report = ["-NoProfile", "-NonInteractive", "-Command", Report];

        var lowered = await ExternalTool.RunAsync(PowerShell, report, Generous, limits: new ExternalToolLimits { BelowNormalPriority = true });
        var normal = await ExternalTool.RunAsync(PowerShell, report, Generous);

        Assert.Equal(["BelowNormal", "BelowNormal"], Lines(lowered.StandardOutput));
        // Control: without the limit both run at the test's own priority.
        using var self = Process.GetCurrentProcess();
        Assert.Equal([self.PriorityClass.ToString(), self.PriorityClass.ToString()], Lines(normal.StandardOutput));

        static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    [Fact]
    public async Task The_tool_inherits_the_environment_with_the_given_changes()
    {
        // Set in the test process, as a variable of the user's would be; names no other test uses.
        var removed = "FLEVERT_TEST_REMOVED_" + Guid.NewGuid().ToString("N");
        var added = "FLEVERT_TEST_ADDED_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(removed, "inherited");
        try
        {
            // Windows names are case-insensitive, so the removal is given in another case.
            var changed = await ExternalTool.RunAsync(Cmd, ["/c", "set FLEVERT_TEST_"], Generous,
                environment: new Dictionary<string, string?> { [removed.ToLowerInvariant()] = null, [added] = "given" });
            var unchanged = await ExternalTool.RunAsync(Cmd, ["/c", "set FLEVERT_TEST_"], Generous);

            Assert.DoesNotContain(removed, changed.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"{added}=given", changed.StandardOutput, StringComparison.Ordinal);
            // Control: without changes the tool sees the variable, and only the variable.
            Assert.Contains($"{removed}=inherited", unchanged.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(added, unchanged.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(removed, null);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    [InlineData("A\0B")]
    public async Task A_variable_name_windows_cannot_hold_is_refused(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalTool.RunAsync(Cmd, ["/c", "exit 0"], Generous,
            environment: new Dictionary<string, string?> { [name] = "x" }));
    }

    [Fact]
    public async Task A_value_with_a_null_character_is_refused()
    {
        // "\0" ends an entry of the environment block: the rest would become another variable.
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalTool.RunAsync(Cmd, ["/c", "exit 0"], Generous,
            environment: new Dictionary<string, string?> { ["FLEVERT_TEST"] = "x\0OTHER=y" }));
    }

    [Fact]
    public async Task A_tool_windows_refuses_under_the_one_process_limit_runs_again_without_it()
    {
        // Review DD-4: on Windows 11 the console host joins the job without counting; elsewhere it might count, and the
        // assignment fails with ERROR_NOT_ENOUGH_QUOTA (1816) after Windows has ended the tool. The seam refuses once.
        var assignments = 0;
        void RefuseFirst(System.Runtime.InteropServices.SafeHandle job, Process process)
        {
            if (++assignments == 1)
                throw new System.ComponentModel.Win32Exception(1816);
            ExternalTool.AssignToJob(job, process);
        }

        var result = await ExternalTool.RunAsync(Cmd, ["/c", Ping, "-n", "1", "127.0.0.1"], Generous, afterStart: null,
            limits: new ExternalToolLimits { SingleProcess = true }, assign: RefuseFirst);

        Assert.Equal(2, assignments);
        Assert.True(result.SingleProcessLimitDropped);
        // Without the limit, cmd could start ping.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("127.0.0.1", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_refusal_to_join_the_job_still_stops_the_run()
    {
        var ping = 0;

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => ExternalTool.RunAsync(Ping, ["-n", "30", "127.0.0.1"],
            Generous, afterStart: null, limits: new ExternalToolLimits { SingleProcess = true },
            assign: (_, process) =>
            {
                ping = process.Id;
                throw new System.ComponentModel.Win32Exception(87);
            }));

        Assert.NotEqual(0, ping);
        Assert.False(IsRunning(ping), "a tool that could not join its job is still running");
    }

    [Fact]
    public async Task Runs_in_the_tools_own_folder_by_default()
    {
        var result = await ExternalTool.RunAsync(Cmd, ["/c", "cd"], Generous);

        Assert.Equal(Environment.SystemDirectory, result.StandardOutput.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task Runs_in_the_given_folder()
    {
        await ExternalTool.RunAsync(Cmd, ["/c", "echo x>marker.txt"], Generous, workingDirectory: _dir.Path);

        Assert.True(File.Exists(_dir.Combine("marker.txt")));
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData(@"system32\cmd.exe")]
    [InlineData(@"\Windows\System32\cmd.exe")]
    [InlineData(@"C:cmd.exe")]
    public async Task A_tool_path_that_is_not_absolute_is_refused(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalTool.RunAsync(path, [], Generous));
    }

    [Fact]
    public async Task A_tool_that_does_not_exist_is_refused()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            ExternalTool.RunAsync(_dir.Combine("missing.exe"), [], Generous));
    }

    [Fact]
    public async Task A_script_is_refused_because_windows_would_run_it_through_cmd()
    {
        var script = _dir.File("tool.cmd", "@echo hello");

        await Assert.ThrowsAsync<ArgumentException>(() => ExternalTool.RunAsync(script, [], Generous));
    }

    [Fact]
    public async Task A_working_folder_that_is_not_absolute_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ExternalTool.RunAsync(Cmd, ["/c", "cd"], Generous, workingDirectory: "relative"));
    }

    [Fact]
    public async Task An_already_cancelled_run_throws_without_waiting()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExternalTool.RunAsync(Ping, ["-n", "30", "127.0.0.1"], Generous, cancellationToken: new CancellationToken(canceled: true)));
    }

    private static async Task<(int Child, int Grandchild)> ReadPids(string file, Task run)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (run.IsCompleted)
                await run; // surfaces the failure
            try
            {
                var fields = File.ReadAllText(file).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length == 2)
                    return (int.Parse(fields[0], CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture));
            }
            catch (IOException) { }

            await Task.Delay(50);
        }

        throw new TimeoutException("the tool never wrote its process ids");
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The ping.exe processes whose parent is <paramref name="parentId"/> (the parent may have exited already).</summary>
    private static List<int> PingsStartedBy(int parentId)
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcessesByName("PING"))
        {
            using (process)
            {
                try
                {
                    if (NtQueryInformationProcess(process.Handle, 0, out var info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                        && info.InheritedFromUniqueProcessId == parentId)
                        found.Add(process.Id);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Exited meanwhile, or not ours to open.
                }
            }
        }

        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int informationClass, out ProcessBasicInformation information, int length, out int returnLength);

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan within)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed >= within)
                return false;
            await Task.Delay(25);
        }

        return true;
    }
}
