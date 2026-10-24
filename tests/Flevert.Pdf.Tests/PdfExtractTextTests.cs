using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Flevert.Core;
using Flevert.Ocr;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/pdf.md, P3: <c>Convert > Text</c>, with the worker of the flevert.exe this solution built.</summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfExtractTextTests : IDisposable
{
    private const string NoTextMessage = "This PDF has no text, and Flevert found none in the pictures of its pages.";

    // What Tesseract says when it fails on a picture (OcrException).
    private const string TooSlowImageMessage = "Recognising text in this image took too long: it may be damaged, or made to stall OCR tools.";
    private const string ToolFailedImageMessage =
        "Flevert's OCR tool stopped on this image without finishing: the image may be damaged, or made to crash OCR tools.";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PdfExtractText Op() => new(TestPdf.Worker);

    [Fact]
    public void It_is_convert_to_text_in_the_pdf_menu_after_the_pictures()
    {
        var op = PdfOperations.All.OfType<PdfExtractText>().Single();

        Assert.Equal(("pdf.extract-text", "Convert to/Text"), (op.Id, op.MenuPath));
        Assert.Equal([".pdf"], op.Extensions);
        Assert.Equal(1, op.MaxParallel);
        // v1.2.3: PNG pictures moved to the Shift-only menu (Convert to PNG images).
        Assert.Equal(["Convert to/JPG images", "Convert to/Text"], PdfRenderOperations.All.Select(o => o.MenuPath));
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
        var text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        Assert.True(text == "Xin chào Việt Nam\r\nĐây là dòng thứ hai\r\n\fTrang hai: tiếng Việt có dấu\r\n\fTrang ba\r\n",
            string.Join(' ', text.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture))));
    }

    [WindowsFact]
    public async Task A_vietnamese_scan_comes_out_as_the_text_tesseract_reads_page_by_page()
    {
        string[] pages = ["Cộng hòa xã hội chủ nghĩa Việt Nam\nĐộc lập - Tự do - Hạnh phúc", "Hợp đồng mua bán hàng hóa\nSố 12 năm 2026"];
        var input = _dir.Combine("scan.pdf");
        await TestOcr.WriteScan(input, pages);

        var reports = new List<FileProgress>();

        var output = await Op().RunAsync(input, new SyncProgress(reports.Add), CancellationToken.None);

        // The worker counts the pages it reads, then the pages it draws for tesseract, then each page tesseract has read.
        Assert.Equal([("page", 1, 2), ("page", 2, 2), ("page picture", 1, 2), ("page picture", 2, 2), ("scanned page", 1, 2), ("scanned page", 2, 2)],
            reports.Select(r => (r.Unit, r.Position, r.Total)));
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var read = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3).Split('\f');
        Assert.Equal(2, read.Length);
        for (var i = 0; i < 2; i++)
            Assert.True(TestOcr.Similarity(pages[i], read[i]) >= 0.95, $"Page {i + 1}: '{read[i]}'");
        Assert.All(read, page => Assert.EndsWith("\r\n", page, StringComparison.Ordinal));
        Assert.Equal(["scan.pdf", "scan.txt"], TestPdf.FileNames(_dir.Path));
    }

    [WindowsFact]
    public async Task A_page_with_text_keeps_its_own_and_a_scanned_page_beside_it_gets_what_tesseract_reads()
    {
        var text = _dir.Combine("text.pdf");
        TestTextPdf.Write(text, "Trang này đã có chữ thật");
        var scan = _dir.Combine("scan.pdf");
        await TestOcr.WriteScan(scan, "Hóa đơn bán hàng số 12345");
        var input = _dir.Combine("mixed.pdf");
        await Qpdf.NextToApp.MergeAsync([text, scan], input, CancellationToken.None);

        var output = await Op().RunAsync(input, CancellationToken.None);

        var read = (await File.ReadAllTextAsync(output)).Split('\f');
        Assert.Equal(2, read.Length);
        Assert.Equal("Trang này đã có chữ thật\r\n", read[0]);
        Assert.True(TestOcr.Similarity("Hóa đơn bán hàng số 12345", read[1]) >= 0.95, read[1]);
    }

    [WindowsFact]
    public async Task A_pdf_without_text_even_in_its_pictures_is_skipped_and_leaves_nothing()
    {
        var input = _dir.Combine("scan.pdf");
        TestPdf.WriteBlank(input, 3);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(NoTextMessage, ex.Message);
        Assert.Equal(["scan.pdf"], TestPdf.FileNames(_dir.Path));
    }

    [WindowsFact]
    public async Task Through_the_runner_a_pdf_without_text_is_skipped_not_failed()
    {
        var input = _dir.Combine("scan.pdf");
        TestPdf.WriteBlank(input, 1);
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Skipped);
        Assert.Equal(NoTextMessage, result.Error);
    }

    [LinuxFact]
    public async Task On_linux_a_pdf_without_text_is_skipped_without_tesseract_as_before_ocr()
    {
        var input = _dir.Combine("scan.pdf");
        TestPdf.WriteBlank(input, 3);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfRenderWorker.NoTextMessage, ex.Message);
        Assert.Equal(["scan.pdf"], TestPdf.FileNames(_dir.Path));
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
        // More than 10 characters a page, white space aside: none is read as a scan.
        TestTextPdf.Write(input, "Trang số một của hồ sơ", "Trang số hai của hồ sơ", "Trang số ba của hồ sơ");
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
    [InlineData("Xin\u00A0chào\u2009Việt\u202FNam", "Xin chào Việt Nam")]
    [InlineData("a\u2000b\u200Ac\u205Fd\u3000e", "a b c d e")]
    // The Ogham space mark is a visible stroke, not white space: kept.
    [InlineData("a\u1680b", "a\u1680b")]
    public void A_pages_text_is_written_with_crlf_and_without_what_cannot_be_read(string pdfium, string written)
    {
        Assert.Equal(written, PdfRenderWorker.PageText(pdfium));
    }

    // v1.4.0 review R7: v1.3.0 wrote pdfium's text whatever the scans held; one page tesseract cannot read must not lose it.
    [WindowsTheory]
    [InlineData("timeout")]
    [InlineData("no tesseract")]
    [InlineData("no tessdata")]
    public async Task A_scanned_page_tesseract_cannot_read_keeps_what_pdfium_has_and_a_note_names_it(string failure)
    {
        var input = _dir.Combine("mixed.pdf");
        TestTextPdf.Write(input, "Trang này đã có chữ thật", "");
        var op = Op(_ => throw failure switch
        {
            "timeout" => new OcrException(TooSlowImageMessage),
            "no tesseract" => new FileNotFoundException("tesseract.exe is missing."),
            _ => new DirectoryNotFoundException("tessdata folder is missing."),
        });
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(op, [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded, result.Error);
        var read = (await File.ReadAllTextAsync(result.Output!)).Split('\f');
        Assert.Equal(2, read.Length);
        Assert.Equal("Trang này đã có chữ thật\r\n", read[0]);
        Assert.Equal("", read[1].Trim());
        Assert.StartsWith("The text of scanned page 2 could not be read.", result.Note, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Only_the_scanned_pages_tesseract_cannot_read_are_named_and_the_others_get_its_text()
    {
        var input = _dir.Combine("mixed.pdf");
        TestTextPdf.Write(input, "Trang có chữ thật", "", "", "");
        var op = Op(page => page == 3 ? Task.FromResult("Chữ trong ảnh trang ba") : throw new OcrException(ToolFailedImageMessage));
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(op, [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded, result.Error);
        var read = (await File.ReadAllTextAsync(result.Output!)).Split('\f');
        Assert.Equal(["Trang có chữ thật\r\n", "", "Chữ trong ảnh trang ba\r\n", ""], read.Select((page, i) => i is 1 or 3 ? page.Trim() : page));
        Assert.Equal("The text of scanned pages 2, 4 could not be read. Flevert's OCR tool stopped on a page of this PDF without "
            + "finishing: the page may be damaged, or made to crash OCR tools.", result.Note);
    }

    [WindowsFact]
    public async Task A_pdf_without_text_whose_scans_tesseract_cannot_read_fails_with_the_error_for_a_pdf_and_leaves_nothing()
    {
        var input = _dir.Combine("scan.pdf");
        TestPdf.WriteBlank(input, 2);
        var op = Op(_ => throw new OcrException(TooSlowImageMessage));

        var ex = await Assert.ThrowsAsync<OcrException>(() => op.RunAsync(input, CancellationToken.None));

        Assert.Equal("Recognising text in a page of this PDF took too long: it may be damaged, or made to stall OCR tools.", ex.Message);
        Assert.Equal(["scan.pdf"], TestPdf.FileNames(_dir.Path));
    }

    [WindowsFact]
    public async Task A_pdf_without_text_and_without_tesseract_fails_saying_tesseract_is_missing()
    {
        var input = _dir.Combine("scan.pdf");
        TestPdf.WriteBlank(input, 1);
        var op = Op(_ => throw new FileNotFoundException("tesseract.exe is missing."));

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => op.RunAsync(input, CancellationToken.None));

        Assert.Equal("tesseract.exe is missing.", ex.Message);
        Assert.Equal(["scan.pdf"], TestPdf.FileNames(_dir.Path));
    }

    [WindowsFact]
    public async Task Cancelling_while_tesseract_reads_cancels_rather_than_counting_the_page_as_unread()
    {
        var input = _dir.Combine("mixed.pdf");
        TestTextPdf.Write(input, "Trang này đã có chữ thật", "");
        var op = Op(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => op.RunAsync(input, CancellationToken.None));

        Assert.Equal(["mixed.pdf"], TestPdf.FileNames(_dir.Path));
    }

    // v1.4.0 review R16: drawing pages at 300 DPI takes a while; the bar must not sit at "page 2 of 2" meanwhile.
    [WindowsFact]
    public async Task The_pages_drawn_for_tesseract_are_counted_between_the_pages_read_and_the_scans_read()
    {
        var input = _dir.Combine("mixed.pdf");
        TestTextPdf.Write(input, "Trang số một", "");
        var reports = new List<FileProgress>();

        await Op(_ => Task.FromResult("Chữ của trang hai")).RunAsync(input, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal([("page", 1, 2), ("page", 2, 2), ("page picture", 1, 2), ("page picture", 2, 2), ("scanned page", 1, 1)],
            reports.Select(r => (r.Unit, r.Position, r.Total)));
    }

    // v1.4.0 review R12: deleting the temporary file and making it again let another account in a shared folder put its
    // own file or link there in between.
    [WindowsFact]
    public async Task The_text_of_scans_is_written_into_the_temporary_file_the_worker_made_never_deleting_it()
    {
        var input = _dir.Combine("mixed.pdf");
        TestTextPdf.Write(input, "Trang này đã có chữ thật", "");
        var deleted = new ConcurrentQueue<string>();
        var renamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(_dir.Path, "*.partial") { NotifyFilter = NotifyFilters.FileName };
        watcher.Deleted += (_, e) => deleted.Enqueue(e.Name!);
        watcher.Renamed += (_, _) => renamed.TrySetResult();
        watcher.EnableRaisingEvents = true;

        var output = await Op(_ => Task.FromResult("Chữ của trang hai")).RunAsync(input, CancellationToken.None);

        // Events come in order: a delete would come before the rename to the output's name.
        await renamed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(deleted);
        Assert.Equal("Trang này đã có chữ thật\r\n\fChữ của trang hai\r\n", await File.ReadAllTextAsync(output));
        Assert.Equal([0xEF, 0xBB, 0xBF], (await File.ReadAllBytesAsync(output))[..3]);
    }

    // A PdfExtractText whose tesseract reads the scanned page numbered from 1 with readPage.
    private static PdfExtractText Op(Func<int, Task<string>> readPage) => new(TestPdf.Worker)
    {
        ReadPicture = (picture, _) => readPage(int.Parse(Path.GetFileNameWithoutExtension(picture)["page-".Length..], CultureInfo.InvariantCulture)),
    };

    private sealed class SyncProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }
}
