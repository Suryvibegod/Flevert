using System.Security.Cryptography;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

public sealed class PdfMergeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    private static PdfMerge Op() => (PdfMerge)MenuOperations.All.Single(op => op.Id == "pdf.merge");

    private string Pdf(string name, params int[] widths)
    {
        var path = _dir.Combine(name);
        WriteWidths(path, widths);
        return path;
    }

    private string CopyFixture(string fixture, string name)
    {
        var path = _dir.Combine(name);
        File.Copy(Fixture(fixture), path);
        return path;
    }

    [Fact]
    public void Is_named_and_placed_as_in_the_spec()
    {
        var op = Op();

        Assert.Equal("pdf.merge", op.Id);
        Assert.Equal("Merge PDFs", op.MenuPath);
        Assert.Equal([".pdf"], op.Extensions);
    }

    [Fact]
    public async Task Joins_every_page_of_every_pdf_in_the_order_given()
    {
        string[] inputs = [Pdf("a.pdf", 101, 102), Pdf("b.pdf", 201), Pdf("c.pdf", 301, 302, 303)];

        var output = await Op().RunAsync(inputs, null, CancellationToken.None);

        Assert.Equal(_dir.Combine("a (merged).pdf"), output);
        Assert.Equal([101, 102, 201, 301, 302, 303], PageWidths(output));
    }

    [Fact]
    public async Task Through_the_runner_the_pages_come_in_explorers_natural_order()
    {
        // "file 2" before "file 10", as Explorer shows them; given in another order.
        var ten = Pdf("file 10.pdf", 1001, 1002);
        var one = Pdf("file 1.pdf", 101);
        var two = Pdf("file 2.pdf", 201, 202, 203);

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(Op(), [ten, two, one], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(_dir.Combine("file 1 (merged).pdf"), result.Output);
        Assert.Equal([101, 201, 202, 203, 1001, 1002], PageWidths(result.Output!));
    }

    [Fact]
    public async Task The_page_count_is_the_sum_and_each_page_keeps_its_content()
    {
        // Real content: three-colours.pdf's red, green and blue pages and rotations.pdf's turned pages.
        string[] inputs = [CopyFixture("three-colours.pdf", "colours.pdf"), CopyFixture("rotations.pdf", "scan.pdf")];

        var output = await Op().RunAsync(inputs, null, CancellationToken.None);

        using var pdf = PdfDocument.Open(output);
        Assert.Equal(7, pdf.PageCount);
        Assert.Equal((255, 0, 0, 255), Pixel(pdf.Render(0, 10), 2, 2));
        Assert.Equal((0, 255, 0, 255), Pixel(pdf.Render(1, 10), 2, 2));
        Assert.Equal((0, 0, 255, 255), Pixel(pdf.Render(2, 10), 2, 2));
        Assert.Equal([Corner.TopLeft, Corner.TopRight, Corner.BottomRight, Corner.BottomLeft],
            Enumerable.Range(3, 4).Select(page => MarkCorner(pdf, page)));
    }

    [Fact]
    public async Task One_pdf_is_refused_with_a_message_saying_to_select_two()
    {
        var input = Pdf("alone.pdf", 100);

        var ex = await Assert.ThrowsAsync<BatchInputException>(() => Op().RunAsync([input], null, CancellationToken.None));

        Assert.Equal("Select at least two PDFs to merge.", ex.Message);
        Assert.Null(ex.InputPath);
        Assert.Equal(["alone.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task The_output_never_replaces_a_file()
    {
        string[] inputs = [Pdf("report.pdf", 100), Pdf("appendix.pdf", 200)];

        var first = await Op().RunAsync(inputs, null, CancellationToken.None);
        var second = await Op().RunAsync(inputs, null, CancellationToken.None);

        Assert.Equal(_dir.Combine("report (merged).pdf"), first);
        Assert.Equal(_dir.Combine("report (merged) (1).pdf"), second);
    }

    [Fact]
    public async Task The_originals_are_left_byte_for_byte_unchanged()
    {
        string[] inputs = [CopyFixture("rotations.pdf", "a.pdf"), CopyFixture("three-colours.pdf", "b.pdf")];
        var hashes = inputs.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToList();
        var written = inputs.Select(File.GetLastWriteTimeUtc).ToList();

        await Op().RunAsync(inputs, null, CancellationToken.None);

        Assert.Equal(hashes, inputs.Select(path => SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Equal(written, inputs.Select(File.GetLastWriteTimeUtc));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_password_protected_pdf_fails_the_merge_naming_it_and_leaves_nothing(int at)
    {
        var inputs = new List<string> { Pdf("a.pdf", 100), Pdf("b.pdf", 200) };
        var locked = CopyFixture("password-protected.pdf", "locked.pdf");
        inputs.Insert(at, locked);

        var ex = await Assert.ThrowsAsync<BatchInputException>(() => Op().RunAsync(inputs, null, CancellationToken.None));

        Assert.Equal(locked, ex.InputPath);
        Assert.Equal(PdfException.NeedsPasswordMessage, ex.Message);
        Assert.Equal(PdfError.PasswordRequired, Assert.IsType<PdfException>(ex.InnerException).Error);
        Assert.Equal(["a.pdf", "b.pdf", "locked.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task Through_the_runner_the_failure_names_the_password_protected_pdf()
    {
        string[] inputs = [Pdf("a.pdf", 100), CopyFixture("password-protected.pdf", "Hóa đơn khóa.pdf")];

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(Op(), inputs, null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.False(result.Succeeded);
        Assert.Contains("Hóa đơn khóa.pdf", result.Error, StringComparison.Ordinal);
        Assert.Contains(PdfException.NeedsPasswordMessage, result.Error, StringComparison.Ordinal);
        Assert.Equal(["Hóa đơn khóa.pdf", "a.pdf"], FileNames(_dir.Path));
    }

    [Theory]
    [InlineData("This is a text file someone renamed to .pdf.\r\n")]
    [InlineData("%PDF-1.7\n1 0 obj\n<< /Type /Catalog")]
    public async Task A_file_that_is_not_a_pdf_fails_the_merge_naming_it(string content)
    {
        string[] inputs = [Pdf("a.pdf", 100), _dir.File("notes.pdf", content), Pdf("c.pdf", 300)];

        var ex = await Assert.ThrowsAsync<BatchInputException>(() => Op().RunAsync(inputs, null, CancellationToken.None));

        Assert.Equal(inputs[1], ex.InputPath);
        Assert.Equal("This file is not a PDF, or it is damaged.", ex.Message);
        Assert.Equal(["a.pdf", "c.pdf", "notes.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task A_pdf_larger_than_the_limit_is_named()
    {
        string[] inputs = [Pdf("a.pdf", 100), Pdf("big.pdf", 200)];
        File.AppendAllText(inputs[1], new string(' ', 4_000));
        var op = new PdfMerge(new Qpdf(Qpdf.NextToApp.ExePath) { MaxInputBytes = 3_000 });

        var ex = await Assert.ThrowsAsync<BatchInputException>(() => op.RunAsync(inputs, null, CancellationToken.None));

        Assert.Equal(inputs[1], ex.InputPath);
        Assert.Equal(PdfError.TooLarge, Assert.IsType<PdfException>(ex.InnerException).Error);
    }

    [Fact]
    public async Task Pdfs_together_larger_than_the_limit_are_refused_since_qpdf_holds_them_all_at_once()
    {
        // Each is under the limit, the three together are not: the memory cap and the timeout go by the sum as well.
        string[] inputs = [Pdf("a.pdf", 100), Pdf("b.pdf", 200), Pdf("c.pdf", 300)];
        var limit = inputs.Max(path => new FileInfo(path).Length) + 1;
        Assert.True(inputs.Sum(path => new FileInfo(path).Length) > limit);
        var op = new PdfMerge(new Qpdf(Qpdf.NextToApp.ExePath) { MaxInputBytes = limit });

        var ex = await Assert.ThrowsAsync<PdfException>(() => op.RunAsync(inputs, null, CancellationToken.None));

        Assert.Equal(PdfError.TooLarge, ex.Error);
        Assert.StartsWith("Together these PDFs are larger than", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["a.pdf", "b.pdf", "c.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task Works_on_pdfs_whose_folder_and_names_are_not_ascii_or_are_past_260_characters()
    {
        var folder = @"\\?\" + _dir.Path;
        while (folder.Length < 320)
            folder = Path.Combine(folder, "thư mục con rất dài để vượt quá giới hạn MAX_PATH");
        Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "Hóa đơn \U0001F9FE tháng 1.pdf");
        var second = Path.Combine(_dir.Path, "Chuyến đi Đà Lạt — 中文.pdf");
        WriteWidths(first, 111);
        WriteWidths(second, 222, 223);

        var output = await Op().RunAsync([first, second], null, CancellationToken.None);

        Assert.Equal(Path.Combine(folder, "Hóa đơn \U0001F9FE tháng 1 (merged).pdf"), output);
        Assert.Equal([111, 222, 223], PageWidths(output));
    }

    [Fact]
    public async Task Cancelling_stops_qpdf_and_leaves_no_partial_file()
    {
        // About 13 s of qpdf work in few pages (the page limit refuses many pages): long enough to cancel in the middle.
        var big = _dir.Combine("big.pdf");
        MaliciousPdf.WriteManyObjects(big, 1_000_000);
        string[] inputs = [big, Pdf("small.pdf", 100)];
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op().RunAsync(inputs, null, cancel.Token));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
        Assert.Equal(["big.pdf", "small.pdf"], FileNames(_dir.Path));
    }
}
