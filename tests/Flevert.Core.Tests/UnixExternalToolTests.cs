using System.Diagnostics;
using System.Text;

namespace Flevert.Core.Tests;

/// <summary>
/// ExternalTool on Linux, with the system's own tools in place of the cmd, PowerShell and ping of ExternalToolTests: the
/// same guarantees, kept without a job object. The tool runs in a process group of its own (setsid), which is ended when
/// the run returns; a memory limit is an address-space limit (prlimit). docs/specs/linux.md.
/// </summary>
public sealed class UnixExternalToolTests : IDisposable
{
    private const string Sh = "/bin/sh";
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Returns_the_exit_code_and_both_outputs_separately()
    {
        var result = await ExternalTool.RunAsync(Sh, ["-c", "echo out; echo err >&2; exit 3"], Generous);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("out\n", result.StandardOutput);
        Assert.Equal("err\n", result.StandardError);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Writes_the_given_bytes_to_standard_input_and_closes_it()
    {
        // cat ends only when its input is closed.
        var result = await ExternalTool.RunAsync("/bin/cat", [], Generous, standardInput: Encoding.UTF8.GetBytes("mật khẩu\n"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("mật khẩu\n", result.StandardOutput);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_tool_that_never_reads_its_input_does_not_hang_the_caller()
    {
        var result = await ExternalTool.RunAsync(Sh, ["-c", "exit 0"], Generous, standardInput: new byte[4 * 1024 * 1024]);

        Assert.Equal(0, result.ExitCode);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Keeps_at_most_one_megabyte_of_each_output_and_still_lets_the_tool_finish()
    {
        var result = await ExternalTool.RunAsync(Sh, ["-c", "head -c 5000000 /dev/zero | tr '\\0' a; head -c 5000000 /dev/zero | tr '\\0' b >&2"], Generous);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ExternalTool.MaxCapturedBytes, result.StandardOutput.Length);
        Assert.Equal(ExternalTool.MaxCapturedBytes, result.StandardError.Length);
        Assert.True(result.StandardOutputTruncated && result.StandardErrorTruncated);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_tool_that_runs_too_long_is_killed_and_reported_as_a_timeout()
    {
        var pidFile = _dir.Combine("pid");
        var timer = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => ExternalTool.RunAsync(Sh, ["-c", $"echo $$ > '{pidFile}'; sleep 60"], TimeSpan.FromSeconds(1)));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}");
        Assert.False(IsRunning(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture)));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Cancelling_kills_the_tool_and_what_it_started_before_returning()
    {
        var pids = _dir.Combine("pids");
        using var cancel = new CancellationTokenSource();
        var run = ExternalTool.RunAsync(Sh, ["-c", $"sleep 60 & echo $$ $! > '{pids}.tmp'; mv '{pids}.tmp' '{pids}'; sleep 60"], Generous,
            cancellationToken: cancel.Token);
        var (child, grandchild) = await ReadPids(pids, run);
        Assert.True(IsRunning(child) && IsRunning(grandchild), "the tool or the process it started is not running");

        var timer = Stopwatch.StartNew();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(IsRunning(child), "RunAsync returned while the tool was still running");
        Assert.True(await WaitUntil(() => !IsRunning(grandchild), TimeSpan.FromSeconds(2)), "the process the tool started outlived the cancellation");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task What_the_tool_leaves_running_is_ended_when_it_exits()
    {
        // The shell exits at once; the sleep it started keeps its output pipe open, so a caller that only waited for the
        // end of output would wait for the sleep (60 s) instead of returning.
        var pidFile = _dir.Combine("pid");
        var timer = Stopwatch.StartNew();

        var result = await ExternalTool.RunAsync(Sh, ["-c", $"sleep 60 & echo $! > '{pidFile}'"], TimeSpan.FromSeconds(20));

        Assert.Equal(0, result.ExitCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
        var left = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(await WaitUntil(() => !IsRunning(left), TimeSpan.FromSeconds(2)), "the sleep started by the tool is still running");
    }

    // The tool is set to die with flevert (PR_SET_PDEATHSIG), which Linux ties to the thread that started it: started
    // from a thread that then ends (a thread-pool thread can retire while the tool runs), it would die with that thread.
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_tool_outlives_the_thread_that_asked_for_it()
    {
        // The thread ends only once the tool runs (its marker exists), so the death signal is surely set by then: a thread
        // that ends sooner has its children handed to another thread, and the signal never comes.
        var running = _dir.Combine("running");
        Task<ExternalToolResult>? run = null;
        var caller = new Thread(() =>
        {
            run = ExternalTool.RunAsync(Sh, ["-c", $"touch '{running}'; sleep 1; echo alive"], Generous);
            var waited = Stopwatch.StartNew();
            while (!File.Exists(running) && waited.Elapsed < TimeSpan.FromSeconds(20))
                Thread.Sleep(10);
        });
        caller.Start();
        caller.Join();

        var result = await run!;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("alive\n", result.StandardOutput);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_tool_refused_memory_past_its_limit_is_reported_as_over_the_limit()
    {
        // dd asks for its whole 512 MB block at once; under the limit the allocation fails and dd says so.
        string[] allocate = ["-c", "dd if=/dev/zero of=/dev/null bs=512M count=1 status=none && echo allocated"];

        var limited = await ExternalTool.RunAsync(Sh, allocate, Generous, limits: new ExternalToolLimits { MaxMemoryBytes = 200 * 1024 * 1024 });
        var free = await ExternalTool.RunAsync(Sh, allocate, Generous);
        var roomy = await ExternalTool.RunAsync(Sh, allocate, Generous, limits: new ExternalToolLimits { MaxMemoryBytes = 2048L * 1024 * 1024 });

        Assert.True(limited.MemoryLimitExceeded, $"the run under the limit was not reported ({limited.ExitCode}: {limited.StandardError})");
        Assert.DoesNotContain("allocated", limited.StandardOutput, StringComparison.Ordinal);
        // Control: a limit the tool stays under reports nothing, and the allocation succeeds.
        Assert.Equal("allocated", roomy.StandardOutput.Trim());
        Assert.False(roomy.MemoryLimitExceeded);
        // Control: the same allocation without a limit succeeds, and nothing is reported.
        Assert.Equal("allocated", free.StandardOutput.Trim());
        Assert.False(free.MemoryLimitExceeded);
    }

    // pdfium says nothing when refused memory: its allocator aborts. Python stands in, taking most of the allowance and
    // then dying of the same signal; a kill (as on cancel) or a crash with little memory taken is not about memory.
    [SkippableTheory]
    [SupportedOSPlatform("linux")]
    [InlineData(330, "ABRT", true)]
    [InlineData(330, "KILL", false)]
    [InlineData(10, "ABRT", false)]
    public async Task A_tool_that_dies_silently_near_its_limit_is_reported_as_over_the_limit(int megabytes, string signal, bool over)
    {
        Skip.IfNot(OperatingSystem.IsLinux() && File.Exists("/usr/bin/python3"), "Linux with Python only");
        var script = $"import os, signal, time; b = bytearray({megabytes} << 20); time.sleep(0.5); os.kill(os.getpid(), signal.SIG{signal})";

        var result = await ExternalTool.RunAsync("/usr/bin/python3", ["-I", "-c", script], Generous,
            limits: new ExternalToolLimits { MaxMemoryBytes = 400 * 1024 * 1024 });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(over, result.MemoryLimitExceeded);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_failure_that_is_not_about_memory_is_not_reported_as_over_the_limit()
    {
        var result = await ExternalTool.RunAsync(Sh, ["-c", "echo 'not a PDF' >&2; exit 2"], Generous,
            limits: new ExternalToolLimits { MaxMemoryBytes = 200 * 1024 * 1024 });

        Assert.Equal(2, result.ExitCode);
        Assert.False(result.MemoryLimitExceeded);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_tool_inherits_the_environment_with_the_given_changes()
    {
        var removed = "FLEVERT_TEST_REMOVED_" + Guid.NewGuid().ToString("N");
        var added = "FLEVERT_TEST_ADDED_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(removed, "inherited");
        try
        {
            var changed = await ExternalTool.RunAsync("/usr/bin/env", [], Generous,
                environment: new Dictionary<string, string?> { [removed] = null, [added] = "given" });
            var unchanged = await ExternalTool.RunAsync("/usr/bin/env", [], Generous);

            Assert.DoesNotContain(removed, changed.StandardOutput, StringComparison.Ordinal);
            Assert.Contains($"{added}=given", changed.StandardOutput, StringComparison.Ordinal);
            // Control: without changes the tool sees the variable, and only the variable.
            Assert.Contains($"{removed}=inherited", unchanged.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(added, unchanged.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(removed, null);
        }
    }

    [LinuxTheory]
    [SupportedOSPlatform("linux")]
    [InlineData("")]
    [InlineData("A=B")]
    [InlineData("A\0B")]
    public async Task A_variable_name_the_environment_cannot_hold_is_refused(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalTool.RunAsync(Sh, ["-c", "exit 0"], Generous,
            environment: new Dictionary<string, string?> { [name] = "x" }));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_value_with_a_null_character_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalTool.RunAsync(Sh, ["-c", "exit 0"], Generous,
            environment: new Dictionary<string, string?> { ["FLEVERT_TEST"] = "x\0OTHER=y" }));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Runs_in_the_tools_own_folder_by_default()
    {
        var result = await ExternalTool.RunAsync("/bin/pwd", [], Generous);

        Assert.Equal("/bin", Path.GetFullPath(result.StandardOutput.Trim()).Replace("/usr/bin", "/bin", StringComparison.Ordinal));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Runs_in_the_given_folder()
    {
        var result = await ExternalTool.RunAsync(Sh, ["-c", "pwd"], Generous, workingDirectory: _dir.Path);

        Assert.Equal(_dir.Path, result.StandardOutput.Trim());
    }

    [LinuxTheory]
    [SupportedOSPlatform("linux")]
    [InlineData("sh")]
    [InlineData("bin/sh")]
    public async Task A_tool_path_that_is_not_absolute_is_refused(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalTool.RunAsync(path, ["-c", "exit 0"], Generous));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_tool_that_does_not_exist_is_refused()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => ExternalTool.RunAsync(_dir.Combine("missing"), [], Generous));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_working_folder_that_is_not_absolute_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => ExternalTool.RunAsync(Sh, ["-c", "exit 0"], Generous, workingDirectory: "relative"));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task An_already_cancelled_run_throws_without_waiting()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalTool.RunAsync(Sh, ["-c", "sleep 60"], Generous, cancellationToken: cancel.Token));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Each_line_reaches_the_callback_while_the_tool_still_runs()
    {
        // The tool waits for the callback to have seen its first line before it writes the second.
        var seen = _dir.Combine("seen");
        var lines = new List<string>();
        var script = $"echo first; i=0; while [ ! -e '{seen}' ] && [ $i -lt 400 ]; do sleep 0.05; i=$((i+1)); done; " +
                     $"if [ -e '{seen}' ]; then echo second; else echo 'waited in vain'; fi";

        var result = await ExternalTool.RunAsync(Sh, ["-c", script], Generous, standardOutputLine: line =>
        {
            lines.Add(line);
            if (line == "first")
                File.WriteAllText(seen, "");
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["first", "second"], lines);
        Assert.Equal("first\nsecond\n", result.StandardOutput);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Line_breaks_are_removed_and_order_is_kept()
    {
        var lines = new List<string>();

        await ExternalTool.RunAsync(Sh, ["-c", "printf 'a\\r\\nb\\n\\nc'"], Generous, standardOutputLine: lines.Add);

        Assert.Equal(["a", "b", "", "c"], lines);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_very_long_line_is_cut_and_the_next_one_still_arrives()
    {
        var lines = new List<string>();

        await ExternalTool.RunAsync(Sh, ["-c", "head -c 100000 /dev/zero | tr '\\0' x; printf '\\nnext\\n'"], Generous, standardOutputLine: lines.Add);

        Assert.Equal([new string('x', ExternalTool.MaxLineChars), "next"], lines);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_callback_that_throws_ends_the_run_with_its_exception()
    {
        var timer = Stopwatch.StartNew();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ExternalTool.RunAsync(Sh, ["-c", "echo boom; sleep 30"], Generous,
            standardOutputLine: _ => throw new InvalidOperationException("bad line")));

        Assert.Equal("bad line", thrown.Message);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}: the tool was not killed");
    }

    private static async Task<(int Child, int Grandchild)> ReadPids(string file, Task run)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(file))
        {
            if (run.IsCompleted)
                await run; // surfaces why the tool ended early
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(20), "the tool did not write its pids");
            await Task.Delay(20);
        }

        var parts = File.ReadAllText(file).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    // A zombie (state Z) has ended; it only waits for its parent to collect it.
    private static bool IsRunning(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > timeout)
                return false;
            await Task.Delay(20);
        }

        return true;
    }
}
