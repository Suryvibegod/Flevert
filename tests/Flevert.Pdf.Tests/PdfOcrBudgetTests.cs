using System.Globalization;
using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary>
/// docs/specs/ocr.md: which pages the worker draws for tesseract, and what that may take. Only the scanned pages count
/// against the budget (<see cref="PdfRenderBudget"/>), and a page is a scan by the same test in the worker as in
/// <see cref="PdfExtractText"/>: at most <see cref="PdfExtractText.ScannedChars"/> characters, white space aside.
/// </summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfOcrBudgetTests : IDisposable
{
    // 2 400 points is 10 000 pixels a side at 300 DPI, the most a page renders at: 100 megapixels a page, so 51 such
    // pages pass the 5 000 megapixels of PdfRenderBudget.MaxTotalPixels.
    private const int LargeSide = 2400;
    private const int LargePages = 52;

    private const string TooManyScannedPagesMessage =
        "This PDF has too many scanned pages to read: their pictures together would be more than 5000 megapixels.";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // tesseract reads the blank page: Windows only, as OCR is.
    [WindowsFact]
    public async Task A_long_text_pdf_with_one_blank_page_still_converts_to_text()
    {
        var input = _dir.Combine("report.pdf");
        WriteLongReportWithABlankPage(input);

        var output = await new PdfExtractText(TestPdf.Worker).RunAsync(input, CancellationToken.None);

        var pages = (await File.ReadAllTextAsync(output)).Split('\f');
        Assert.Equal(LargePages + 1, pages.Length);
        Assert.Equal("Chapter 1 of the long report\r\n", pages[0]);
        Assert.Equal($"Chapter {LargePages} of the long report\r\n", pages[LargePages - 1]);
        Assert.Equal("\r\n", pages[^1]);
    }

    [Fact]
    public async Task Only_the_scanned_pages_are_drawn_and_counted()
    {
        var input = _dir.Combine("report.pdf");
        WriteLongReportWithABlankPage(input);
        var folder = Directory.CreateDirectory(_dir.Combine("pictures")).FullName;

        var (total, ocrPages) = await TestPdf.Worker.RenderOcrPagesAsync(input, folder, PdfMakeSearchable.OcrDpi, null, CancellationToken.None);

        Assert.Equal(LargePages + 1, total);
        Assert.Equal([LargePages + 1], ocrPages);
        Assert.Equal([$"page-{LargePages + 1}.png"], TestPdf.FileNames(folder));
    }

    [Fact]
    public async Task Too_many_scanned_pages_are_refused_before_any_is_drawn_in_words_about_reading_them()
    {
        var input = _dir.Combine("scans.pdf");
        WritePages(input, [.. Enumerable.Repeat(new Page(LargeSide, LargeSide, null), 51)]);
        var folder = Directory.CreateDirectory(_dir.Combine("pictures")).FullName;

        var ex = await Assert.ThrowsAsync<PdfException>(() =>
            TestPdf.Worker.RenderOcrPagesAsync(input, folder, PdfMakeSearchable.OcrDpi, null, CancellationToken.None));

        Assert.Equal(PdfError.TooLarge, ex.Error);
        Assert.Equal(TooManyScannedPagesMessage, ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public async Task A_page_is_a_scan_by_the_same_count_in_the_worker_as_in_convert_to_text()
    {
        // 8 letters in 15 characters; 11 letters; 10 letters; 3 letters among 12 spaces; 11 letters among spaces.
        string[] texts = ["A B C D E F G H", "ABCDEFGHIJK", "ABCDEFGHIJ", "1    2    3", "AB CD EF GH IJ K"];
        var input = _dir.Combine("short.pdf");
        WritePages(input, [.. texts.Select(text => new Page(595, 842, text))]);
        var textFile = _dir.Combine("short.txt");
        await TestPdf.Worker.ExtractTextAsync(input, textFile, null, CancellationToken.None);
        var extracted = (await File.ReadAllTextAsync(textFile)).Split('\f');
        var folder = Directory.CreateDirectory(_dir.Combine("pictures")).FullName;

        var (_, ocrPages) = await TestPdf.Worker.RenderOcrPagesAsync(input, folder, PdfMakeSearchable.OcrDpi, null, CancellationToken.None);

        // What Convert to > Text reads again from the pictures is what the worker draws for it, no more, no less.
        var scannedByText = Enumerable.Range(1, texts.Length)
            .Where(page => extracted[page - 1].Count(c => !char.IsWhiteSpace(c)) <= PdfExtractText.ScannedChars);
        Assert.Equal([1, 3, 4], scannedByText);
        Assert.Equal([1, 3, 4], ocrPages);
    }

    [Fact]
    public void The_budget_of_scanned_pages_speaks_of_reading_them()
    {
        var ex = Assert.Throws<PdfException>(() => PdfRenderBudget.CheckOcr(PdfRenderBudget.MaxTotalPixels + 1, long.MaxValue));
        Assert.Equal(PdfError.TooLarge, ex.Error);
        Assert.Equal(TooManyScannedPagesMessage, ex.Message);

        // One A4 page at 300 DPI fits anywhere with room; a PNG takes about a byte a pixel, with 64 MB the disk keeps free.
        const long A4 = 2480L * 3508;
        Assert.Null(PdfRenderBudget.CheckOcr(A4, long.MaxValue));
        Assert.Null(PdfRenderBudget.CheckOcr(A4, null));
        Assert.Equal("There is not enough free space to read the scanned pages of this PDF: their pictures need about 72 MB, and 10 MB is free.",
            PdfRenderBudget.CheckOcr(A4, 10L << 20));
    }

    [Fact]
    public void Every_page_is_checked_in_order_before_the_first_is_drawn()
    {
        using var session = Session(PdfWorkerTask.OcrPages, "pages 2", "checked 1", "checked 2", "page 1 10 10", "wrote 1", "page 2 10 10", "ocr 2", "wrote 2", "done 2");

        Assert.Equal([2], session.OcrPages);
    }

    [Theory]
    [InlineData("Pictures", "pages 2", "checked 1")] // not this task's line
    [InlineData("OcrPages", "checked 1")] // before the page count
    [InlineData("OcrPages", "pages 2", "checked 2")] // out of order
    [InlineData("OcrPages", "pages 2", "checked 1", "checked 1")] // twice
    [InlineData("OcrPages", "pages 1", "checked 1", "checked 2")] // past the last page
    [InlineData("OcrPages", "pages 2", "checked 1", "page 1 10 10", "checked 2")] // once a page is drawn
    public void A_checked_line_out_of_place_breaks_the_protocol(string task, params string[] lines)
    {
        var ex = Record.Exception(() => Session(Enum.Parse<PdfWorkerTask>(task), lines).Dispose());

        Assert.IsType<PdfWorker.ProtocolException>(ex);
    }

    private static PdfWorker.Session Session(PdfWorkerTask task, params string[] lines)
    {
        var session = new PdfWorker.Session(TestPdf.Worker, TimeSpan.FromMinutes(1), "read", null, CancellationToken.None) { Task = task };
        foreach (var line in lines)
            session.OnLine(line);
        return session;
    }

    private sealed record Page(int Width, int Height, string? Text);

    // Pages of real text, then one blank page: a report of 52 pages 2 400 points a side, 5 200 megapixels at 300 DPI.
    private static void WriteLongReportWithABlankPage(string path) => WritePages(path,
    [
        .. Enumerable.Range(1, LargePages).Select(n => new Page(LargeSide, LargeSide, $"Chapter {n} of the long report")),
        new Page(595, 842, null),
    ]);

    // Each page with its line of Helvetica (ASCII only), or none: a page without a character is what a blank scan reads as.
    private static void WritePages(string path, IReadOnlyList<Page> pages)
    {
        // 1 catalog, 2 page tree, 3 the font; then each page and its content.
        var objects = new List<byte[]>
        {
            ToolPdf.Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            ToolPdf.Ascii("<< /Type /Pages /Count " + pages.Count.ToString(CultureInfo.InvariantCulture) + " /Kids ["
                + string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => (4 + (2 * i)).ToString(CultureInfo.InvariantCulture) + " 0 R")) + "] >>"),
            ToolPdf.Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        };
        for (var i = 0; i < pages.Count; i++)
        {
            var (width, height, text) = pages[i];
            objects.Add(ToolPdf.Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (2 * i)} 0 R >>")));
            objects.Add(ToolPdf.Stream("", ToolPdf.Ascii(text is null
                ? ""
                : string.Create(CultureInfo.InvariantCulture, $"BT /F1 24 Tf 72 {height - 100} Td ({text}) Tj ET"))));
        }

        ToolPdf.Write(path, objects);
    }
}
