using System.Diagnostics;

namespace Flevert.Core.Tests;

/// <summary>
/// The lines of standard output handed to a callback while the tool runs: how a long-running tool (the PDF worker) reports
/// progress to Flevert. docs/specs/pdf.md, T9b.
/// </summary>
public sealed class ExternalToolLineTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Each_line_reaches_the_callback_while_the_tool_still_runs()
    {
        // ping waits about two seconds between the two echoes.
        var lines = new List<(string Line, TimeSpan At)>();
        var timer = Stopwatch.StartNew();

        var result = await ExternalTool.RunAsync(Cmd, ["/c", "echo first& ping -n 3 127.0.0.1 >nul& echo second"], Generous,
            standardOutputLine: line => lines.Add((line, timer.Elapsed)));
        var finished = timer.Elapsed;

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["first", "second"], lines.Select(l => l.Line));
        Assert.True(finished - lines[0].At > TimeSpan.FromSeconds(1), $"the first line came {finished - lines[0].At} before the end");
        // The lines are still in the captured output.
        Assert.Equal("first\r\nsecond\r\n", result.StandardOutput);
    }

    [Fact]
    public async Task Line_breaks_are_removed_and_order_is_kept()
    {
        var lines = new List<string>();

        await ExternalTool.RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::Out.Write(\"a`r`nb`nc`r`n`r`nđ ảnh\")"], Generous,
            standardOutputLine: lines.Add);

        // A last line without a break still counts; an empty line is a line.
        Assert.Equal(["a", "b", "c", "", "đ ảnh"], lines);
    }

    [Fact]
    public async Task A_very_long_line_is_cut_and_the_next_one_still_arrives()
    {
        var lines = new List<string>();

        await ExternalTool.RunAsync(PowerShell,
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('x' * 100000); [Console]::Out.Write(\"`nnext`n\")"], Generous,
            standardOutputLine: lines.Add);

        Assert.Equal([new string('x', ExternalTool.MaxLineChars), "next"], lines);
    }

    [Fact]
    public async Task A_callback_that_throws_ends_the_run_with_its_exception()
    {
        var timer = Stopwatch.StartNew();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ExternalTool.RunAsync(Cmd,
            ["/c", "echo boom& ping -n 30 127.0.0.1 >nul"], Generous,
            standardOutputLine: _ => throw new InvalidOperationException("bad line")));

        Assert.Equal("bad line", thrown.Message);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}: the tool was not killed");
    }
}
