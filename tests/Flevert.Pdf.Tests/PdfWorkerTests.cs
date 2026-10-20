using System.Diagnostics;
using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary>
/// The PDF worker (ADR-0008, "Reading untrusted PDFs in a worker") through the real flevert.exe: what happens when pdfium
/// stalls, runs out of memory, crashes or is cancelled. In the worker collection, so no other test's worker runs meanwhile:
/// these tests find theirs as a child of this test process.
/// </summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfWorkerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_page_that_never_finishes_is_stopped_and_reported_within_the_stall_window()
    {
        // About 40 minutes of pdfium work in one call (TestPdf.WriteStallingPage).
        var input = _dir.Combine("stalled.pdf");
        TestPdf.WriteStallingPage(input);
        var timer = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<PdfException>(() => Png(TestPdf.Worker).RunAsync(input, CancellationToken.None));

        // An A4 page at 150 DPI is 2.2 MP: the product's window is 10 s plus a quarter second per megapixel.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"took {timer.Elapsed}");
        Assert.Equal(PdfError.TooSlow, ex.Error);
        Assert.Equal("Page 1 of this PDF took too long to render: it is too complex, or made to stall PDF readers.", ex.Message);
        Assert.Equal(["stalled.pdf"], Names(_dir.Path));
        Assert.Empty(TestProcesses.Workers());
    }

    [Fact]
    public async Task Cancelling_kills_the_worker_within_two_seconds_and_leaves_nothing()
    {
        var input = _dir.Combine("stalled.pdf");
        TestPdf.WriteStallingPage(input);
        using var cancel = new CancellationTokenSource();
        var rendering = new TaskCompletionSource();
        var progress = new SyncProgress(_ => rendering.TrySetResult());

        var run = Png(TestPdf.Worker).RunAsync(input, progress, cancel.Token);
        await rendering.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var worker = await TestProcesses.WaitForWorkerAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500); // inside pdfium's render call, not between two lines
        var timer = Stopwatch.StartNew();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"took {timer.Elapsed}");
        Assert.True(worker.HasExited, "the worker still runs after the cancel returned");
        Assert.Equal(["stalled.pdf"], Names(_dir.Path)); // no .flevert-….partial folder either
        worker.Dispose();
    }

    [Fact]
    public async Task A_page_needing_more_memory_than_the_cap_fails_with_the_memory_message()
    {
        // 14 400 pt square renders at 10 000 x 10 000 px: 400 MB of pixels, past a 256 MB cap.
        var input = _dir.Combine("poster.pdf");
        TestPdf.WriteSquarePage(input, 14_400);
        var worker = new PdfWorker(TestPdf.FlevertExe) { MaxMemoryBytes = 256UL << 20 };

        var ex = await Assert.ThrowsAsync<PdfException>(() => Png(worker).RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.NeedsTooMuchMemory, ex.Error);
        Assert.Equal("This PDF needs more memory than Flevert allows for it (256 MB): it is too large or too complex to process.", ex.Message);
        Assert.Equal(["poster.pdf"], Names(_dir.Path));
    }

    [Fact]
    public async Task The_same_page_renders_under_the_products_memory_cap()
    {
        // The control for the test above: only the cap differs.
        var input = _dir.Combine("poster.pdf");
        TestPdf.WriteSquarePage(input, 14_400);

        var folder = await new PdfToImages(PdfPageImageFormat.Jpeg, PdfToImages.Dpi, TestPdf.Worker).RunAsync(input, CancellationToken.None);

        Assert.Equal(["poster 001.jpg"], Names(folder));
    }

    [Fact]
    public async Task A_worker_that_dies_midway_fails_the_file_with_a_clear_message_and_the_caller_carries_on()
    {
        var input = _dir.Combine("stalled.pdf");
        TestPdf.WriteStallingPage(input);
        var rendering = new TaskCompletionSource();
        var progress = new SyncProgress(_ => rendering.TrySetResult());

        var run = Png(TestPdf.Worker).RunAsync(input, progress, CancellationToken.None);
        await rendering.Task.WaitAsync(TimeSpan.FromSeconds(30));
        using (var worker = await TestProcesses.WaitForWorkerAsync(TimeSpan.FromSeconds(5)))
            worker.Kill(); // what a crash inside pdfium looks like from outside: the process is gone, nothing said why

        var ex = await Assert.ThrowsAsync<PdfException>(() => run);

        Assert.Equal(PdfError.ReaderFailed, ex.Error);
        Assert.Equal("Flevert's PDF reader stopped on page 1 of this PDF without finishing: the PDF may be damaged, or made to crash PDF readers.", ex.Message);
        Assert.Equal(["stalled.pdf"], Names(_dir.Path));
        // This process is unharmed: the next file converts.
        var good = _dir.Combine("good.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), good);
        Assert.Equal(3, Names(await Png(TestPdf.Worker).RunAsync(good, CancellationToken.None)).Length);
    }

    [Fact]
    public async Task Nothing_about_the_file_is_on_the_worker_command_line()
    {
        // Other processes of the user, and tools that log process starts, can read a command line.
        var input = _dir.Combine("Hồ sơ bệnh án.pdf");
        TestPdf.WriteStallingPage(input);
        using var cancel = new CancellationTokenSource();
        var rendering = new TaskCompletionSource();

        var run = Png(TestPdf.Worker).RunAsync(input, new SyncProgress(_ => rendering.TrySetResult()), cancel.Token);
        try
        {
            await rendering.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var worker = await TestProcesses.WaitForWorkerAsync(TimeSpan.FromSeconds(5));
            var commandLine = TestProcesses.CommandLine(worker);

            Assert.Equal($"\"{TestPdf.FlevertExe}\" pdf-worker", commandLine);
        }
        finally
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
    }

    [Fact]
    public async Task A_file_that_is_not_a_pdf_gives_the_shared_message()
    {
        var input = _dir.File("notes.pdf", "just some text, not a PDF");

        var ex = await Assert.ThrowsAsync<PdfException>(() => Png(TestPdf.Worker).RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.NotPdf, ex.Error);
        Assert.Equal("This file is not a PDF, or it is damaged.", ex.Message);
        Assert.Equal(["notes.pdf"], Names(_dir.Path));
    }

    [Fact]
    public async Task A_missing_worker_fails_with_a_message_naming_it()
    {
        var input = _dir.Combine("colours.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);
        var missing = new PdfWorker(_dir.Combine("flevert.exe"));

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => Png(missing).RunAsync(input, CancellationToken.None));

        Assert.Contains("flevert.exe is missing", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["colours.pdf"], Names(_dir.Path));
    }

    [Fact]
    public async Task Progress_reports_every_page_in_order_with_the_page_count()
    {
        var input = _dir.Combine("many.pdf");
        TestPdf.WriteBlank(input, 12);
        var reports = new List<FileProgress>();

        await new PdfToImages(PdfPageImageFormat.Jpeg, 10, TestPdf.Worker).RunAsync(input, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(Enumerable.Range(1, 12).Select(page => ("page", page, 12)), reports.Select(r => (r.Unit, r.Position, r.Total)));
    }

    private static PdfToImages Png(PdfWorker worker) => new(PdfPageImageFormat.Png, PdfToImages.Dpi, worker);

    private static string[] Names(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    /// <summary>Calls back at once, on the reporting thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class SyncProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value)
        {
            lock (this)
                report(value);
        }
    }
}
