using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Flevert.Core.Tests;

/// <summary>
/// Runs the system's own tools (cmd, findstr, ping, Windows PowerShell) the way Flevert will run qpdf. The tests in this
/// class run one after another, and no other test starts ping.exe, so a ping left running can only come from here.
/// </summary>
public sealed class ExternalToolTests : IDisposable
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
        var before = RunningPings();
        var timer = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            ExternalTool.RunAsync(Ping, ["-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(500)));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2.5), $"took {timer.Elapsed}");
        Assert.Empty(RunningPings().Except(before));
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
        var before = RunningPings();
        var timer = Stopwatch.StartNew();

        var result = await ExternalTool.RunAsync(Cmd, ["/c", "start", "", "/b", Ping, "-n", "30", "127.0.0.1"],
            TimeSpan.FromSeconds(20));

        Assert.Equal(0, result.ExitCode);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
        Assert.True(await WaitUntil(() => !RunningPings().Except(before).Any(), TimeSpan.FromSeconds(2)),
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

    private static HashSet<int> RunningPings()
    {
        var processes = Process.GetProcessesByName("PING");
        var ids = processes.Select(p => p.Id).ToHashSet();
        foreach (var process in processes)
            process.Dispose();
        return ids;
    }

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
