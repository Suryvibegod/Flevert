using System.Diagnostics;

namespace Flevert.Pdf.Tests;

/// <summary>
/// A batch of real flevert.exe processes, one per selected PDF as Explorer starts them, headless: a PDF made to stall the
/// reader fails on its own, and the batch leader survives it and converts the other file (review round 1, DD-H2, SA-M3,
/// CR-4; success criterion 6 of docs/specs/pdf.md).
/// </summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfWorkerBatchTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task A_stalling_pdf_is_refused_and_the_leader_still_converts_the_other_file_of_its_batch()
    {
        var stalled = _dir.Combine("a stalled.pdf");
        TestPdf.WriteStallingPage(stalled);
        var good = _dir.Combine("b good.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), good);
        var timer = Stopwatch.StartNew();

        // The stalling file first, so the leader meets it before the good one (one PDF at a time).
        var processes = new List<Process> { StartRun(stalled) };
        await Task.Delay(300);
        processes.Add(StartRun(good));
        try
        {
            await Task.WhenAll(processes.Select(p => p.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(90));
        }
        finally
        {
            foreach (var process in processes.Where(p => !p.HasExited))
                process.Kill(entireProcessTree: true);
        }

        var logs = string.Join('\n', Directory.GetFiles(_logs.Path).Select(File.ReadAllText));
        // The leader ends normally, saying a file failed; its follower handed its file over and ended at once.
        Assert.True(processes.Select(p => p.ExitCode).Order().SequenceEqual([0, 1]),
            $"exit codes {string.Join(", ", processes.Select(p => p.ExitCode))}\n{logs}");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(40), $"took {timer.Elapsed}");
        Assert.Contains("Page 1 of this PDF took too long to render", logs, StringComparison.Ordinal);
        Assert.Equal(["a stalled.pdf", "b good (pages)", "b good.pdf"],
            Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["b good 001.png", "b good 002.png", "b good 003.png"], TestPdf.FileNames(_dir.Combine("b good (pages)")));
        processes.ForEach(p => p.Dispose());
    }

    private Process StartRun(string input)
    {
        var start = new ProcessStartInfo(TestPdf.FlevertExe) { UseShellExecute = false };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("pdf.to-png");
        start.ArgumentList.Add(input);
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        start.Environment["FLEVERT_HEADLESS"] = "1";
        // One batch even on a busy machine: the second process must reach the leader within the idle window.
        start.Environment["FLEVERT_BATCH_IDLE_MS"] = "3000";
        return Process.Start(start)!;
    }
}
