using System.Text;
using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/pdf.md, P3: <c>Convert > Text</c>, with the worker of the flevert.exe this solution built.</summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfExtractTextTests : IDisposable
{
    private const string NoTextMessage =
        "This PDF has no text to extract: it may be a scanned document (Flevert does not recognise text in pictures).";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PdfExtractText Op() => new(TestPdf.Worker);

    [Fact]
    public void It_is_convert_text_in_the_pdf_menu_after_the_pictures()
    {
        var op = PdfOperations.All.OfType<PdfExtractText>().Single();

        Assert.Equal(("pdf.extract-text", "Convert/Text"), (op.Id, op.MenuPath));
        Assert.Equal([".pdf"], op.Extensions);
        Assert.Equal(1, op.MaxParallel);
        Assert.Equal(["Convert/JPG pages", "Convert/PNG pages", "Convert/Text"], PdfRenderOperations.All.Select(o => o.MenuPath));
    }

    [Fact]
    public async Task Vietnamese_text_comes_out_in_page_order_as_utf8_with_a_bom_crlf_and_a_form_feed_between_pages()
    {
        var input = _dir.Combine("Hóa đơn tháng 1.pdf");
        TestTextPdf.Write(input, "Xin chào Việt Nam\nĐây là dòng thứ hai", "Trang hai: tiếng Việt có dấu", "Trang ba");

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Hóa đơn tháng 1.txt"), output);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(
            "Xin chào Việt Nam\r\nĐây là dòng thứ hai\r\n\fTrang hai: tiếng Việt có dấu\r\n\fTrang ba\r\n",
            new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task A_pdf_without_text_is_skipped_as_a_scan_and_leaves_nothing()
    {
        var input = _dir.Combine("scan.pdf");
        TestPdf.WriteBlank(input, 3);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(NoTextMessage, ex.Message);
        Assert.Equal(["scan.pdf"], TestPdf.FileNames(_dir.Path));
    }

    [Fact]
    public async Task Through_the_runner_a_scan_is_skipped_not_failed()
    {
        var input = _dir.Combine("scan.pdf");
        TestPdf.WriteBlank(input, 1);
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Skipped);
        Assert.Equal(NoTextMessage, result.Error);
    }

    [Fact]
    public async Task A_page_without_text_between_pages_with_text_keeps_its_place()
    {
        var input = _dir.Combine("mixed.pdf");
        TestTextPdf.Write(input, "Một", "", "Ba");

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal("Một\r\n\f\r\n\fBa\r\n", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Reports_progress_once_per_page_in_order()
    {
        var input = _dir.Combine("three.pdf");
        TestTextPdf.Write(input, "1", "2", "3");
        var reports = new List<FileProgress>();

        await Op().RunAsync(input, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal([1, 2, 3], reports.Select(r => r.Position));
        Assert.All(reports, r => Assert.Equal(("page", 3), (r.Unit, r.Total)));
    }

    [Fact]
    public async Task A_password_protected_pdf_fails_with_the_message_naming_the_fix_and_leaves_nothing()
    {
        var input = _dir.Combine("locked.pdf");
        File.Copy(TestPdf.Fixture("password-protected.pdf"), input);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.PasswordRequired, ex.Error);
        Assert.Equal(["locked.pdf"], TestPdf.FileNames(_dir.Path));
    }

    [Fact]
    public async Task An_existing_text_file_is_never_replaced()
    {
        var input = _dir.Combine("notes.pdf");
        TestTextPdf.Write(input, "mới");
        File.WriteAllText(_dir.Combine("notes.txt"), "mine");

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("notes (1).txt"), output);
        Assert.Equal("mine", File.ReadAllText(_dir.Combine("notes.txt")));
    }

    [Fact]
    public void More_text_than_the_limit_fails_as_too_large_before_it_is_written()
    {
        using var output = new MemoryStream();
        var started = new List<int>();

        var ex = Assert.Throws<PdfException>(() => PdfRenderWorker.WriteText(_ => new string('a', 40), 5, output, maxBytes: 100, started.Add, _ => { }));

        Assert.Equal(PdfError.TooLarge, ex.Error);
        Assert.Equal("The text of this PDF is larger than 100 MB, the most Flevert writes.", ex.Message);
        // The BOM and two pages of 42 bytes, the third would pass 100: it is not written.
        Assert.Equal([1, 2, 3], started);
        Assert.Equal(3 + 42 + 43, output.Length);
    }

    [Theory]
    [InlineData("a\nb", "a\r\nb")]
    [InlineData("a\rb\r\n\r\n  ", "a\r\nb")]
    [InlineData("a\0b\fc￾d￿", "abcd")]
    [InlineData(" \r\n\t ", "")]
    public void A_pages_text_is_written_with_crlf_and_without_what_cannot_be_read(string pdfium, string written)
    {
        Assert.Equal(written, PdfRenderWorker.PageText(pdfium));
    }

    private sealed class SyncProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }
}
