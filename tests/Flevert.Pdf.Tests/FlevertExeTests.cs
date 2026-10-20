using System.ComponentModel;
using System.Diagnostics;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>
/// Runs the real flevert.exe: covers the catalog wiring, finding qpdf.exe next to flevert.exe, and qpdf ending with
/// flevert.exe. One class, so its tests never run at the same time (they look for that qpdf.exe's processes).
/// </summary>
public sealed class FlevertExeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Flevert_exe_rotates_a_pdf_with_the_qpdf_beside_it()
    {
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(FlevertExe)!, "qpdf.exe")), "qpdf.exe is not next to flevert.exe");
        var input = _dir.Combine("Hóa đơn.pdf");
        File.Copy(Fixture("rotations.pdf"), input);

        using var flevert = StartRun("pdf.rotate-right", input);
        try
        {
            await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            if (!flevert.HasExited)
                flevert.Kill(entireProcessTree: true);
        }

        Assert.True(flevert.ExitCode == 0, string.Join('\n', Directory.GetFiles(_logs.Path).Select(File.ReadAllText)));
        using var pdf = PdfDocument.Open(_dir.Combine("Hóa đơn (rotated).pdf"));
        Assert.Equal([Corner.TopRight, Corner.BottomRight, Corner.BottomLeft, Corner.TopLeft],
            Enumerable.Range(0, pdf.PageCount).Select(page => MarkCorner(pdf, page)));
    }

    [Fact]
    public async Task Killing_flevert_exe_while_qpdf_runs_ends_qpdf_too()
    {
        // About 13 s of qpdf work (50 MB): far longer than the wait below, so qpdf ending in time means it was killed.
        var input = _dir.Combine("big.pdf");
        WriteBlank(input, 500_000, resources: true);
        var qpdfPath = Path.Combine(Path.GetDirectoryName(FlevertExe)!, "qpdf.exe");

        using var flevert = StartRun("pdf.rotate-right", input);
        Process? qpdf = null;
        try
        {
            var waited = Stopwatch.StartNew();
            while ((qpdf = FindRunning(qpdfPath)) is null && !flevert.HasExited && waited.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(20);
            Assert.True(qpdf is not null, $"qpdf.exe never started (flevert.exe exited: {flevert.HasExited})");

            // Not the tree: only what Windows does when flevert.exe dies (closing its handles) may end qpdf.
            flevert.Kill(entireProcessTree: false);
            await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            await Task.WhenAny(qpdf.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.True(qpdf.HasExited, "qpdf.exe kept running after flevert.exe was killed");
        }
        finally
        {
            if (!flevert.HasExited)
                flevert.Kill(entireProcessTree: true);
            if (qpdf is { HasExited: false })
                qpdf.Kill();
            qpdf?.Dispose();
        }
    }

    private Process StartRun(string operationId, string input)
    {
        var start = new ProcessStartInfo(FlevertExe) { UseShellExecute = false };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(operationId);
        start.ArgumentList.Add(input);
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        start.Environment["FLEVERT_HEADLESS"] = "1";
        start.Environment["FLEVERT_BATCH_IDLE_MS"] = "100";
        return Process.Start(start)!;
    }

    private static Process? FindRunning(string exePath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                // Exited meanwhile, or not ours to inspect.
            }

            process.Dispose();
        }

        return null;
    }

    private static string FlevertExe
    {
        get
        {
            // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = output.Parent!.Name;
            var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            var exe = Path.Combine(root, "src", "Flevert.Cli", "bin", configuration, output.Name, "flevert.exe");
            Assert.True(File.Exists(exe), $"Build the solution first; missing {exe}");
            return exe;
        }
    }
}
