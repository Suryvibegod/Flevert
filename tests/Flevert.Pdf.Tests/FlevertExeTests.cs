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

    [Fact]
    public async Task Six_flevert_exe_processes_one_per_image_combine_them_into_one_pdf_in_natural_order()
    {
        // Explorer starts one flevert.exe per selected file, in no reliable order (ADR-0002); "photo 9" sorts before
        // "photo 10" as Explorer shows them. Each image has its own width, so a page out of place shows.
        var images = new[]
        {
            TestImages.Picture(_dir.Combine("photo 1.jpg"), 96, 64, ImageMagick.MagickFormat.Jpeg),
            TestImages.Picture(_dir.Combine("photo 2.png"), 104, 64, ImageMagick.MagickFormat.Png),
            CopyFixture("portrait-gps.heic", "photo 3.heic"), // 240 x 320 upright
            TestImages.Picture(_dir.Combine("photo 9.webp"), 112, 64, ImageMagick.MagickFormat.WebP),
            TestImages.Picture(_dir.Combine("photo 10.gif"), 120, 64, ImageMagick.MagickFormat.Gif),
            TestImages.Picture(_dir.Combine("photo 11.tiff"), 128, 64, ImageMagick.MagickFormat.Tiff),
        };
        int[] startOrder = [4, 1, 5, 0, 3, 2];

        // One batch even on a busy machine: every process must reach the leader within the idle window.
        var processes = startOrder.Select(i => StartRun("pdf.from-images", images[i], idleMs: "3000")).ToList();
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
        Assert.True(processes.All(p => p.ExitCode == 0), $"exit codes {string.Join(", ", processes.Select(p => p.ExitCode))}\n{logs}");
        Assert.Equal(
            images.Select(Path.GetFileName).Append("photo 1 (+5).pdf").Order(StringComparer.Ordinal),
            Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        using var pdf = PdfDocument.Open(_dir.Combine("photo 1 (+5).pdf"));
        Assert.Equal([96, 104, 240, 112, 120, 128], Enumerable.Range(0, pdf.PageCount).Select(i => pdf.Render(i, 150).Width));
        processes.ForEach(p => p.Dispose());
    }

    [Fact]
    public async Task Three_flevert_exe_processes_one_per_pdf_merge_them_into_one_pdf_in_natural_order()
    {
        // Explorer starts one flevert.exe per selected file, in no reliable order; "file 2" sorts before "file 10".
        string[] pdfs = [_dir.Combine("file 1.pdf"), _dir.Combine("file 2.pdf"), _dir.Combine("file 10.pdf")];
        WriteWidths(pdfs[0], 101);
        WriteWidths(pdfs[1], 201, 202);
        WriteWidths(pdfs[2], 1001, 1002, 1003);
        int[] startOrder = [2, 0, 1];

        var processes = startOrder.Select(i => StartRun("pdf.merge", pdfs[i], idleMs: "3000")).ToList();
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
        Assert.True(processes.All(p => p.ExitCode == 0), $"exit codes {string.Join(", ", processes.Select(p => p.ExitCode))}\n{logs}");
        Assert.Equal(
            ["file 1 (merged).pdf", "file 1.pdf", "file 10.pdf", "file 2.pdf"],
            Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal([101, 201, 202, 1001, 1002, 1003], PageWidths(_dir.Combine("file 1 (merged).pdf")));
        processes.ForEach(p => p.Dispose());
    }

    [Fact]
    public async Task Flevert_exe_splits_a_pdf_into_a_folder_of_pages()
    {
        var input = _dir.Combine("Hóa đơn.pdf");
        WriteWidths(input, 100, 200, 300);

        using var flevert = StartRun("pdf.split", input);
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
        var folder = _dir.Combine("Hóa đơn (pages)");
        Assert.Equal([100], PageWidths(Path.Combine(folder, "Hóa đơn 001.pdf")));
        Assert.Equal([200], PageWidths(Path.Combine(folder, "Hóa đơn 002.pdf")));
        Assert.Equal([300], PageWidths(Path.Combine(folder, "Hóa đơn 003.pdf")));
    }

    [Fact]
    public async Task Flevert_exe_removes_the_metadata_of_a_pdf()
    {
        var input = CopyFixture("metadata.pdf", "Hóa đơn.pdf");

        using var flevert = StartRun("pdf.strip-metadata", input);
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
        var output = _dir.Combine("Hóa đơn (no metadata).pdf");
        Assert.Equal(new Dictionary<string, string> { ["/Producer"] = "u:Flevert" }, await ReadInfo(output));
        var text = await QdfText(output);
        Assert.DoesNotContain("XMP Author", text, StringComparison.Ordinal);
        Assert.DoesNotContain("app.alert", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Attached secret text", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flevert_exe_compresses_a_pdf()
    {
        var input = _dir.Combine("Hóa đơn.pdf");
        WriteBloated(input, 20_000);

        using var flevert = StartRun("pdf.compress", input);
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
        var output = _dir.Combine("Hóa đơn (compressed).pdf");
        Assert.True(new FileInfo(output).Length < new FileInfo(input).Length / 3);
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.PageCount);
    }

    private string CopyFixture(string fixture, string name)
    {
        var path = _dir.Combine(name);
        File.Copy(Fixture(fixture), path);
        return path;
    }

    private Process StartRun(string operationId, string input, string idleMs = "100")
    {
        var start = new ProcessStartInfo(FlevertExe) { UseShellExecute = false };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(operationId);
        start.ArgumentList.Add(input);
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        start.Environment["FLEVERT_HEADLESS"] = "1";
        start.Environment["FLEVERT_BATCH_IDLE_MS"] = idleMs;
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
