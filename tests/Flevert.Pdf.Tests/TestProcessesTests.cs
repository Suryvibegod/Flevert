using System.Diagnostics;

namespace Flevert.Pdf.Tests;

/// <summary>
/// The helper the worker tests find their worker with. A process that is exiting can still be in the process list for a
/// moment, and Windows then refuses to read its command line (0xC000010A). That moment cannot be timed from a test, so
/// these hand the helper a worker that has exited (still open, so its id is still valid) as if the list had held it.
/// </summary>
[Collection(WorkerProcesses.Name)]
public sealed class TestProcessesTests
{
    [Fact]
    public async Task An_exited_worker_that_is_still_listed_is_not_a_worker()
    {
        using var exited = await ExitedWorkerAsync();

        Assert.Empty(TestProcesses.Workers([exited]));
    }

    [Fact]
    public async Task The_command_line_of_an_exited_process_is_unknown()
    {
        using var exited = await ExitedWorkerAsync();

        Assert.Null(TestProcesses.CommandLine(exited));
    }

    // A real `flevert.exe pdf-worker`, child of this process: it reads its request from standard input, so closing that
    // ends it. The returned object keeps a handle open, which keeps the exited process's id valid.
    private static async Task<Process> ExitedWorkerAsync()
    {
        var start = new ProcessStartInfo(TestPdf.FlevertExe, "pdf-worker")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = Process.Start(start)!;
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return process;
    }
}
