using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/pdf.md, v0.2.2: <c>Split into parts under 10 MB / 25 MB</c>, with a lower limit to keep the PDFs small.</summary>
public sealed class PdfSplitBySizeTests : IDisposable
{
    private const long Limit = 350_000;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PdfSplitBySize Op(long limit = Limit) => new(10, Qpdf.NextToApp) { LimitBytes = limit };

    [Fact]
    public void Two_sizes_are_not_in_the_right_click_menu()
    {
        var ten = new PdfSplitBySize(10, Qpdf.NextToApp);
        var twentyFive = new PdfSplitBySize(25, Qpdf.NextToApp);

        Assert.Equal(("pdf.split-10mb", 10_000_000L), (ten.Id, ten.LimitBytes));
        Assert.Equal(("pdf.split-25mb", 25_000_000L), (twentyFive.Id, twentyFive.LimitBytes));
        Assert.Equal("", ten.MenuPath);
        Assert.Equal([".pdf"], ten.Extensions);
        Assert.Equal(4, ten.MaxParallel);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfSplitBySize(5, Qpdf.NextToApp));
    }

    [Fact]
    public async Task Every_part_is_under_the_limit_and_together_they_hold_every_page_in_order()
    {
        var input = _dir.Combine("Hồ sơ scan.pdf");
        int[] widths = [301, 302, 303, 304, 305, 306, 307, 308, 309, 310, 311, 312];
        ToolPdf.WriteHeavyPages(input, 100_000, widths);
        Assert.True(new FileInfo(input).Length > 3 * Limit);

        var folder = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Hồ sơ scan (parts)"), folder);
        var names = FileNames(folder);
        Assert.True(names.Length >= 4, string.Join(", ", names));
        var parts = Enumerable.Range(1, names.Length).Select(part => Path.Combine(folder, $"Hồ sơ scan part {part}.pdf")).ToList();
        Assert.All(parts, part => Assert.True(new FileInfo(part).Length < Limit, $"{part}: {new FileInfo(part).Length}"));
        Assert.Equal(widths, parts.SelectMany(PageWidths));
        // Greedy: no part could have taken the next part's first page (each holds three 100 KB pages here).
        Assert.Equal(4, names.Length);
    }

    [Fact]
    public async Task A_pdf_already_under_the_limit_is_skipped_and_nothing_is_written()
    {
        var input = _dir.Combine("small.pdf");
        ToolPdf.WriteHeavyPages(input, 10_000, 300, 300);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal("This PDF is already under 0.35 MB: there is nothing to split.", ex.Message);
        Assert.Equal(["small.pdf"], FileNames(_dir.Path));
        Assert.Equal("This PDF is already under 10 MB: there is nothing to split.",
            (await Assert.ThrowsAsync<OperationSkippedException>(() => new PdfSplitBySize(10, Qpdf.NextToApp).RunAsync(input, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_page_over_the_limit_on_its_own_fails_naming_it()
    {
        var input = _dir.Combine("big page.pdf");
        ToolPdf.WriteHeavyPages(input, 100_000, 300, 300, 300);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op(80_000).RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.TooLarge, ex.Error);
        Assert.Equal("Page 1 of this PDF is 0.08 MB or more on its own, so it cannot go in a part under 0.08 MB.", ex.Message);
        Assert.Equal(["big page.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task Pages_sharing_one_image_fill_a_part_by_what_they_really_add()
    {
        // Ten pages draw the same 300 KB image and one of 30 KB each: alone a page weighs 330 KB, four together 420 KB.
        var input = _dir.Combine("shared.pdf");
        ToolPdf.WriteSharedImagePages(input, 10, 300_000, 30_000);

        var folder = await Op(450_000).RunAsync(input, CancellationToken.None);

        var parts = FileNames(folder).Select(name => Path.Combine(folder, name)).ToList();
        Assert.All(parts, part => Assert.True(new FileInfo(part).Length < 450_000));
        Assert.Equal([4, 4, 2], parts.Select(part => PageWidths(part).Length));
    }

    [Fact]
    public async Task Many_pages_sharing_one_image_take_few_runs_of_qpdf()
    {
        // 200 pages draw the same 300 KB image and one of 3 KB each: alone a page weighs 303 KB, 200 of them 900 KB. Grown
        // a page at a time, as single pages' sizes suggest, a part took a run of qpdf per page.
        var input = _dir.Combine("logo on every page.pdf");
        ToolPdf.WriteSharedImagePages(input, 200, 300_000, 3_000);
        var op = Op(450_000);

        var folder = await op.RunAsync(input, CancellationToken.None);

        var parts = FileNames(folder).Select(name => Path.Combine(folder, name)).ToList();
        Assert.All(parts, part => Assert.True(new FileInfo(part).Length < 450_000));
        Assert.Equal(200, parts.Sum(part => PageWidths(part).Length));
        Assert.True(op.Written <= (2 * parts.Count) + 16, $"{op.Written} runs of qpdf for {parts.Count} parts");
    }

    [Fact]
    public async Task Splitting_stops_with_a_clear_error_before_the_pages_fill_the_disk()
    {
        // Split apart, the 200 pages of 303 KB would be 60 MB in the temporary folder, on a disk with 2.7 MB free.
        var input = _dir.Combine("shared.pdf");
        ToolPdf.WriteSharedImagePages(input, 200, 300_000, 3_000);
        var length = new FileInfo(input).Length;
        var scratch = _dir.Combine("scratch");
        Directory.CreateDirectory(scratch);
        var op = new PdfSplitBySize(10, Qpdf.NextToApp) { LimitBytes = 450_000, ScratchRoot = scratch, FreeSpace = _ => 3 * length, FreeMarginBytes = 0 };

        var ex = await Assert.ThrowsAsync<IOException>(() => op.RunAsync(input, CancellationToken.None));

        Assert.StartsWith("There is not enough free space", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["shared.pdf"], FileNames(_dir.Path));
        Assert.Equal([scratch], Directory.GetDirectories(_dir.Path));
        Assert.Empty(Directory.GetFileSystemEntries(scratch));
    }

    [Fact]
    public async Task Parts_that_do_not_fit_beside_the_pdf_are_not_written()
    {
        var input = _dir.Combine("Hồ sơ.pdf");
        ToolPdf.WriteHeavyPages(input, 100_000, 300, 301, 302, 303, 304, 305);
        var scratch = Path.GetFullPath(Path.GetTempPath());
        // Room in the temporary folder, none beside the PDF.
        var op = new PdfSplitBySize(10, Qpdf.NextToApp)
        {
            LimitBytes = Limit,
            FreeSpace = folder => Path.GetFullPath(folder).StartsWith(scratch, StringComparison.OrdinalIgnoreCase) && !Path.GetFullPath(folder).StartsWith(_dir.Path, StringComparison.OrdinalIgnoreCase) ? null : 100_000,
        };

        var ex = await Assert.ThrowsAsync<IOException>(() => op.RunAsync(input, CancellationToken.None));

        Assert.StartsWith("There is not enough free space", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["Hồ sơ.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task Progress_counts_pages_placed_in_parts()
    {
        var input = _dir.Combine("progress.pdf");
        ToolPdf.WriteHeavyPages(input, 100_000, 300, 300, 300, 300, 300, 300);
        var reports = new List<FileProgress>();

        await Op().RunAsync(input, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(new FileProgress("page", 1, 6), reports[0]);
        Assert.Equal(6, reports[^1].Total);
        Assert.All(reports, report => Assert.Equal("page", report.Unit));
    }

    private sealed class SyncProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }
}
