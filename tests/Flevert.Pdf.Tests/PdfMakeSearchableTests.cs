using System.Diagnostics;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/ocr.md: <c>Make searchable (OCR)</c>, success criteria 1 and 2, with the worker and tesseract this solution built.</summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfMakeSearchableTests : IDisposable
{
    private const string FirstPage = "Cộng hòa xã hội chủ nghĩa Việt Nam\nĐộc lập - Tự do - Hạnh phúc";
    private const string SecondPage = "Hợp đồng mua bán hàng hóa\nSố 12 năm 2026";
    private const string FullPage = FirstPage + "\nHóa đơn bán hàng kiêm phiếu xuất kho\nNgày 21 tháng 10 năm 2026\nKhách hàng: Công ty Cổ phần Công nghệ Mới";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op() => PdfTools.Create("pdf.make-searchable", new Dictionary<string, string>(), Qpdf.NextToApp, Worker);

    [Fact]
    public void It_is_in_the_shift_menu_of_pdf_tools()
    {
        var op = Assert.Single(PdfToolMenu.All, op => op.Id == "pdf.make-searchable");

        Assert.Equal("Make searchable (OCR)", op.MenuPath);
        Assert.Equal([".pdf"], op.Extensions);
    }

    [WindowsFact]
    public async Task A_vietnamese_scan_gets_text_pdfium_reads_back_and_looks_the_same()
    {
        var input = _dir.Combine("Hợp đồng scan.pdf");
        await TestOcr.WriteScan(input, FirstPage, SecondPage);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Hợp đồng scan (searchable).pdf"), output);
        using var before = PdfDocument.Open(input);
        using var after = PdfDocument.Open(output);
        Assert.Equal(2, after.PageCount);
        string[] expected = [FirstPage, SecondPage];
        for (var page = 0; page < 2; page++)
        {
            Assert.Equal(before.GetRenderSize(page, 72), after.GetRenderSize(page, 72));
            var similarity = TestOcr.Similarity(expected[page], after.GetText(page));
            Assert.True(similarity >= 0.95, $"Page {page + 1}: {similarity:P1} for '{after.GetText(page)}'");
            // The text is invisible: the page draws exactly what it drew.
            Assert.True(before.Render(page, 72).Bgra.Span.SequenceEqual(after.Render(page, 72).Bgra.Span), $"Page {page + 1} looks different.");
            TestTextBoxes.AssertTextOverInk(after, page);
        }
    }

    [WindowsFact]
    public async Task A_page_that_has_text_gets_no_second_layer_and_a_scan_page_beside_it_gets_one()
    {
        var text = _dir.Combine("text.pdf");
        TestTextPdf.Write(text, "Trang này đã có chữ thật");
        var scan = _dir.Combine("scan.pdf");
        await TestOcr.WriteScan(scan, FirstPage);
        var input = _dir.Combine("mixed.pdf");
        await Qpdf.NextToApp.MergeAsync([text, scan], input, CancellationToken.None);

        var output = await Op().RunAsync(input, CancellationToken.None);

        using var pdf = PdfDocument.Open(output);
        // Exactly its own text, once: pdfium reads its spaces as other white space.
        Assert.Equal(1, TestOcr.Similarity("Trang này đã có chữ thật", pdf.GetText(0)));
        Assert.True(TestOcr.Similarity(FirstPage, pdf.GetText(1)) >= 0.95, pdf.GetText(1));
    }

    [WindowsTheory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task A_page_shown_turned_gets_its_text_where_the_words_are_drawn(int degrees)
    {
        // A scan saved sideways and turned upright by the page's /Rotate, as scanner apps do: tesseract reads the page
        // as it is shown, and qpdf must lay the text over it the same way.
        var scan = _dir.Combine("scan.pdf");
        await TestOcr.WriteScan(scan, degrees, FirstPage);
        var input = await new PdfRotate(degrees, Qpdf.NextToApp).RunAsync(scan, CancellationToken.None);

        var output = await Op().RunAsync(input, CancellationToken.None);

        using var before = PdfDocument.Open(input);
        using var after = PdfDocument.Open(output);
        Assert.True(before.Render(0, 72).Bgra.Span.SequenceEqual(after.Render(0, 72).Bgra.Span), "The page looks different.");
        var similarity = TestOcr.Similarity(FirstPage, after.GetText(0));
        Assert.True(similarity >= 0.95, $"{similarity:P1} for '{after.GetText(0)}'");
        TestTextBoxes.AssertTextOverInk(after, 0);
    }

    [WindowsTheory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task A_page_scanned_turned_is_turned_upright_and_its_text_read_in_order(int degrees)
    {
        // Nothing turns this page upright: OSD finds the words turned, and the page's /Rotate is turned to show them
        // upright (ocr.md, review R5: as OCRmyPDF --rotate-pages does, nothing re-encoded), with the text over them, so
        // that a viewer reads each line in order. OSD is only sure of a page of text: two lines score about 3, under the
        // threshold of 14, and are read as they are.
        var page = string.Join('\n', Enumerable.Repeat(FullPage, 6));
        var input = _dir.Combine("scan.pdf");
        await TestOcr.WriteScan(input, degrees, page);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal([degrees], await ReadRotations(output));
        var upright = await new PdfRotate(degrees, Qpdf.NextToApp).RunAsync(input, CancellationToken.None);
        using var expected = PdfDocument.Open(upright);
        using var after = PdfDocument.Open(output);
        Assert.True(expected.Render(0, 72).Bgra.Span.SequenceEqual(after.Render(0, 72).Bgra.Span), "The page does not look as the scan turned upright.");
        var similarity = TestOcr.Similarity(page, after.GetText(0));
        Assert.True(similarity >= 0.95, $"{similarity:P1} for '{after.GetText(0)}'");
        TestTextBoxes.AssertTextOverInk(after, 0);
    }

    [WindowsFact]
    public async Task Cancel_while_tesseract_reads_stops_it_and_leaves_nothing()
    {
        var input = _dir.Combine("scan.pdf");
        await TestOcr.WriteScan(input, [.. Enumerable.Repeat(FirstPage + "\n" + SecondPage, 8)]);
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(report =>
        {
            // The worker has drawn the pages and tesseract has read one: cancel while the others are being read.
            if (report.Unit == PdfExtractText.ScannedPageUnit)
                cancel.Cancel();
        });

        var started = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op().RunAsync(input, progress, cancel.Token));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");
        Assert.Equal(["scan.pdf"], FileNames(_dir.Path));
        Assert.Equal(0, TestProcesses.RunningChildren("tesseract"));
    }

    [WindowsFact]
    public async Task A_pdf_that_already_has_text_everywhere_is_skipped_and_leaves_nothing()
    {
        var input = _dir.Combine("text.pdf");
        TestTextPdf.Write(input, "Trang một có đủ chữ", "Trang hai cũng có đủ chữ");

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal("This PDF already has text you can search.", ex.Message);
        Assert.Equal(["text.pdf"], FileNames(_dir.Path));
    }

    private sealed class SyncProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }
}
