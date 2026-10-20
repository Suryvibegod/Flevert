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
        // The tool waits for the callback to have seen its first line before it writes the second, so the proof does not
        // depend on how busy the machine is: had the line arrived only at the end, the tool would report that it waited
        // in vain.
        var seen = Path.Combine(Path.GetTempPath(), $"flevert-line-{Guid.NewGuid():N}");
        var lines = new List<string>();
        try
        {
            var script = "[Console]::Out.WriteLine('first'); [Console]::Out.Flush(); $t = [Diagnostics.Stopwatch]::StartNew(); " +
                $"while (-not (Test-Path -LiteralPath '{seen}') -and $t.Elapsed.TotalSeconds -lt 20) {{ Start-Sleep -Milliseconds 20 }}; " +
                $"if (Test-Path -LiteralPath '{seen}') {{ [Console]::Out.WriteLine('second') }} else {{ [Console]::Out.WriteLine('waited in vain') }}";
            var result = await ExternalTool.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", script], Generous,
                standardOutputLine: line =>
                {
                    lines.Add(line);
                    if (line == "first")
                        File.WriteAllText(seen, "");
                });

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(["first", "second"], lines);
            // The lines are still in the captured output.
            Assert.Equal("first\r\nsecond\r\n", result.StandardOutput);
        }
        finally
        {
            File.Delete(seen);
        }
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
