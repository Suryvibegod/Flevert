using Flevert.Ocr;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>
/// docs/specs/ocr.md, success criteria 1 and 3: a short scanned page in Arial, turned, which OSD finds turned but is
/// unsure of (five lines score 8-9, under the threshold of 14), is shown upright with its text read in order; Convert to >
/// Text reads it right too.
/// </summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfMakeSearchableOrientationTests : IDisposable
{
    // Five lines of 14 point Arial (TestTextPdf), as a short letter has.
    private const string Page = """
        Cộng hòa xã hội chủ nghĩa Việt Nam
        Độc lập - Tự do - Hạnh phúc
        Hóa đơn bán hàng kiêm phiếu xuất kho
        Ngày 21 tháng 10 năm 2026
        Khách hàng: Công ty Cổ phần Công nghệ Mới
        """;

    private readonly TempDir _dir = new();

    // Not inside _dir: the pictures tesseract reads are kept in a plain ASCII folder of their own.
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "flevert-tests", $"ocr-scratch-{Guid.NewGuid():N}");

    public PdfMakeSearchableOrientationTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        _dir.Dispose();
        try { Directory.Delete(_scratch, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private PdfMakeSearchable Op() => new(Qpdf.NextToApp, Worker, Tesseract.NextToApp) { ScratchRoot = _scratch };

    [WindowsTheory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task A_short_sans_serif_page_scanned_turned_is_shown_upright_and_read_in_order(int degrees)
    {
        var input = _dir.Combine("scan.pdf");
        await TestOcr.WriteScan(input, degrees, Page);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal([degrees], await ReadRotations(output));
        var upright = await new PdfRotate(degrees, Qpdf.NextToApp).RunAsync(input, CancellationToken.None);
        using var expected = PdfDocument.Open(upright);
        using var after = PdfDocument.Open(output);
        Assert.True(expected.Render(0, 72).Bgra.Span.SequenceEqual(after.Render(0, 72).Bgra.Span), "The page does not look as the scan turned upright.");
        var similarity = TestOcr.Similarity(Page, after.GetText(0));
        Assert.True(similarity >= 0.95, $"{similarity:P1} for '{after.GetText(0)}'");
        TestTextBoxes.AssertTextOverInk(after, 0);
    }

    [WindowsFact]
    public async Task A_short_upright_page_is_left_as_it_is()
    {
        var input = _dir.Combine("scan.pdf");
        await TestOcr.WriteScan(input, Page);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(0, Assert.Single(await ReadRotations(output)));
        using var before = PdfDocument.Open(input);
        using var after = PdfDocument.Open(output);
        Assert.True(before.Render(0, 72).Bgra.Span.SequenceEqual(after.Render(0, 72).Bgra.Span), "The page looks different.");
        var similarity = TestOcr.Similarity(Page, after.GetText(0));
        Assert.True(similarity >= 0.95, $"{similarity:P1} for '{after.GetText(0)}'");
        TestTextBoxes.AssertTextOverInk(after, 0);
    }

    [WindowsTheory]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Convert_to_text_reads_a_short_turned_scan_right(int degrees)
    {
        var input = _dir.Combine("scan.pdf");
        await TestOcr.WriteScan(input, degrees, Page);

        var output = await new PdfExtractText(Worker, Tesseract.NextToApp) { ScratchRoot = _scratch }.RunAsync(input, CancellationToken.None);

        var text = await File.ReadAllTextAsync(output);
        var similarity = TestOcr.Similarity(Page, text);
        Assert.True(similarity >= 0.95, $"{similarity:P1} for '{text}'");
    }
}
