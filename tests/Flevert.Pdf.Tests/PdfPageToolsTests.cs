using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/pdf.md, v0.2.2, success criterion 2: <c>Remove blank pages</c>.</summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfRemoveBlankPagesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PdfRemoveBlankPages Op() => new(Qpdf.NextToApp, TestPdf.Worker);

    [Fact]
    public void It_is_not_in_the_right_click_menu()
    {
        var op = Op();

        Assert.Equal(("pdf.remove-blank-pages", "", 1), (op.Id, op.MenuPath, op.MaxParallel));
        Assert.Equal([".pdf"], op.Extensions);
    }

    [Fact]
    public async Task A_six_page_scan_loses_its_two_blank_pages_and_keeps_the_page_with_one_small_line()
    {
        var input = _dir.Combine("Bản scan hai mặt.pdf");
        ToolPdf.WriteScan(input, ToolPdf.ScanPage.Text, ToolPdf.ScanPage.Blank, ToolPdf.ScanPage.Text,
            ToolPdf.ScanPage.OneSmallLine, ToolPdf.ScanPage.Blank, ToolPdf.ScanPage.Text);
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(_dir.Combine("Bản scan hai mặt (no blank pages).pdf"), result.Output);
        Assert.Equal([591, 593, 594, 596], PageWidths(result.Output!));
        Assert.Equal("Removed 2 blank pages: 2, 5.", result.Note);
    }

    [Fact]
    public async Task Initials_a_stamp_or_a_comment_keep_a_scanned_page_and_a_shadow_with_dust_does_not()
    {
        var input = _dir.Combine("signed.pdf");
        ToolPdf.WriteScan(input, ToolPdf.ScanPage.Text, ToolPdf.ScanPage.Initials, ToolPdf.ScanPage.Stamp,
            ToolPdf.ScanPage.ShadowAndDust, ToolPdf.ScanPage.Comment, ToolPdf.ScanPage.Blank);
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([591, 592, 593, 595], PageWidths(result.Output!));
        Assert.Equal("Removed 2 blank pages: 4, 6.", result.Note);
    }

    [Fact]
    public async Task A_pdf_without_blank_pages_is_skipped()
    {
        var input = _dir.Combine("full.pdf");
        ToolPdf.WriteScan(input, ToolPdf.ScanPage.Text, ToolPdf.ScanPage.OneSmallLine);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal("This PDF has no blank pages.", ex.Message);
        Assert.Equal(["full.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task A_pdf_of_blank_pages_only_fails_and_nothing_is_removed()
    {
        var input = _dir.Combine("empty.pdf");
        ToolPdf.WriteScan(input, ToolPdf.ScanPage.Blank, ToolPdf.ScanPage.Blank);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal("Every page of this PDF looks blank: nothing was removed.", ex.Message);
        Assert.Equal(["empty.pdf"], FileNames(_dir.Path));
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 5, 7, 8, 9 }, "1-3,5,7-9")]
    [InlineData(new[] { 4 }, "4")]
    [InlineData(new[] { 1, 3 }, "1,3")]
    public void Kept_pages_are_given_to_qpdf_as_ranges(int[] pages, string expected) =>
        Assert.Equal(expected, PdfRemoveBlankPages.Ranges(pages));

    [Fact]
    public void The_note_names_one_page_or_the_first_twenty()
    {
        Assert.Equal("Removed 1 blank page: 4.", PdfRemoveBlankPages.RemovedNote([4]));
        Assert.Equal("Removed 25 blank pages: " + string.Join(", ", Enumerable.Range(1, 20)) + ", ….",
            PdfRemoveBlankPages.RemovedNote([.. Enumerable.Range(1, 25)]));
    }

    // An A4 page drawn at 150 DPI: 1240 x 1754 pixels, white.
    private const int PageWidth = 1240, PageHeight = 1754;

    private static byte[] WhitePage() => Enumerable.Repeat((byte)255, PageWidth * PageHeight * 4).ToArray();

    private static void Ink(byte[] page, int x, int y, int width, int height, byte luma = 0)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
                page[((row * PageWidth) + column) * 4] = page[((row * PageWidth) + column) * 4 + 1] = page[((row * PageWidth) + column) * 4 + 2] = luma;
        }
    }

    private static bool IsBlank(byte[] page) => PdfToolsWorker.IsBlank(page, PageWidth, PageHeight, PdfToolsWorker.BlankDpi);

    [Fact]
    public void A_white_page_is_blank()
    {
        Assert.True(IsBlank(WhitePage()));
    }

    [Fact]
    public void Handwritten_initials_keep_a_page_though_they_are_far_under_a_thousandth_of_it()
    {
        // "N.A." in pen, about 1.2 x 0.6 cm in the bottom right corner: four strokes 3 pixels wide.
        var page = WhitePage();
        Ink(page, 1050, 1600, 3, 35);
        Ink(page, 1050, 1600, 25, 3);
        Ink(page, 1075, 1600, 3, 35);
        Ink(page, 1095, 1600, 3, 35);
        Assert.True(4 * 35 * 3 < PageWidth * PageHeight / 1000); // the old rule took it for blank

        Assert.False(IsBlank(page));
    }

    [Fact]
    public void A_small_stamp_keeps_a_page()
    {
        // A ring of 1.5 cm, 3 pixels thick, in red ink.
        var page = WhitePage();
        for (var y = 0; y < PageHeight; y++)
        {
            for (var x = 0; x < PageWidth; x++)
            {
                var distance = Math.Sqrt(Math.Pow(x - 300, 2) + Math.Pow(y - 300, 2));
                if (distance is >= 41 and < 44)
                    (page[((y * PageWidth) + x) * 4], page[((y * PageWidth) + x) * 4 + 1]) = ((byte)40, (byte)40);
            }
        }

        Assert.False(IsBlank(page));
    }

    [Fact]
    public void A_pencil_note_keeps_a_page()
    {
        // A light grey line of 4 cm, 2 pixels high.
        var page = WhitePage();
        Ink(page, 400, 900, 236, 2, luma: 180);

        Assert.False(IsBlank(page));
    }

    [Fact]
    public void A_scanner_shadow_along_an_edge_and_scattered_dust_leave_a_page_blank()
    {
        var page = WhitePage();
        Ink(page, 0, 0, 3, PageHeight, luma: 90); // the lid's shadow, 3 pixels along the left edge
        Ink(page, 0, PageHeight - 4, PageWidth, 4, luma: 120); // and along the bottom
        var random = new Random(3);
        for (var speck = 0; speck < 60; speck++)
            Ink(page, random.Next(40, PageWidth - 42), random.Next(40, PageHeight - 42), 2, 2, luma: 60);

        Assert.True(IsBlank(page));
    }

}

/// <summary>docs/specs/pdf.md, v0.2.2, success criterion 3: <c>Add page numbers</c>.</summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfPageNumbersTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PdfPageNumbers Op(PdfPageNumberPosition position = PdfPageNumberPosition.BottomCenter, PdfPageNumberStyle style = PdfPageNumberStyle.Number) =>
        new(position, style, Qpdf.NextToApp, TestPdf.Worker);

    [Fact]
    public void It_is_not_in_the_right_click_menu()
    {
        var op = Op();

        Assert.Equal(("pdf.page-numbers", "", 1), (op.Id, op.MenuPath, op.MaxParallel));
    }

    [Fact]
    public async Task Each_page_has_its_number_at_the_bottom_centre_as_text()
    {
        var input = _dir.Combine("Báo cáo.pdf");
        ToolPdf.WriteHalves(input, (595, 842, 0), (595, 842, 0), (612, 792, 0));

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Báo cáo (numbered).pdf"), output);
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(3, pdf.PageCount);
        for (var page = 0; page < 3; page++)
        {
            Assert.Equal((page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), pdf.GetText(page).Trim());
            var ink = Ink(pdf.Render(page, 72), _ => true);
            // Black on the red and blue halves: about 1 cm from the bottom, in the middle.
            Assert.InRange(ink.CenterX, (pdf.GetPageSize(page).Width / 2) - 4, (pdf.GetPageSize(page).Width / 2) + 4);
            Assert.InRange(ink.Bottom, pdf.GetPageSize(page).Height - 30, pdf.GetPageSize(page).Height - 26);
        }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task On_a_turned_page_the_number_is_upright_at_the_bottom_of_the_page_as_shown(int rotate)
    {
        var input = _dir.Combine("xoay.pdf");
        ToolPdf.WriteHalves(input, (400, 600, rotate));

        var output = await Op(style: PdfPageNumberStyle.Words).RunAsync(input, CancellationToken.None);

        using var pdf = PdfDocument.Open(output);
        Assert.Equal("Page 1 of 1", pdf.GetText(0).Trim());
        var shown = pdf.GetPageSize(0);
        var ink = Ink(pdf.Render(0, 72), _ => true);
        Assert.True(ink.Width > 3 * ink.Height, $"the number is {ink.Width} x {ink.Height} px: not upright");
        Assert.InRange(ink.CenterX, (shown.Width / 2) - 4, (shown.Width / 2) + 4);
        Assert.InRange(ink.Bottom, shown.Height - 30, shown.Height - 26);
    }

    [Fact]
    public async Task Top_right_puts_it_about_a_centimetre_from_the_top_and_the_right()
    {
        var input = _dir.Combine("trang.pdf");
        ToolPdf.WriteHalves(input, (595, 842, 90));

        var output = await Op(PdfPageNumberPosition.TopRight, PdfPageNumberStyle.OfTotal).RunAsync(input, CancellationToken.None);

        using var pdf = PdfDocument.Open(output);
        Assert.Equal("1 / 1", pdf.GetText(0).Trim());
        var shown = pdf.GetPageSize(0);
        var ink = Ink(pdf.Render(0, 72), _ => true);
        Assert.InRange(ink.Right, shown.Width - 31, shown.Width - 27);
        Assert.InRange(ink.Top, 27, 31);
    }

    /// <summary>The box of the black pixels (on red and blue, black is only the number) of a page drawn at 72 DPI.</summary>
    internal static (double Left, double Top, double Right, double Bottom, double Width, double Height, double CenterX) Ink(PdfBitmap page, Func<(int X, int Y), bool> where)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < page.Height; y++)
        {
            for (var x = 0; x < page.Width; x++)
            {
                var (r, g, b, _) = Pixel(page, x, y);
                if (r < 100 && g < 100 && b < 100 && where((x, y)))
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x + 1), Math.Max(bottom, y + 1));
            }
        }

        Assert.True(right > 0, "no black on the page");
        return (left, top, right, bottom, right - left, bottom - top, (left + right) / 2.0);
    }
}

/// <summary>docs/specs/pdf.md, v0.2.2, success criterion 3: <c>Add watermark</c>.</summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfWatermarkTests : IDisposable
{
    private const string Text = "Chỉ dùng để nộp hồ sơ xin việc";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PdfWatermark Op(string text = Text) => new(text, Qpdf.NextToApp, TestPdf.Worker);

    [Fact]
    public void It_is_not_in_the_right_click_menu()
    {
        var op = Op();

        Assert.Equal(("pdf.watermark", "", 1), (op.Id, op.MenuPath, op.MaxParallel));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public async Task The_vietnamese_text_is_across_the_page_in_light_grey_and_comes_back_as_text(int rotate)
    {
        var input = _dir.Combine("CCCD.pdf");
        WriteWhitePage(input, rotate);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("CCCD (watermarked).pdf"), output);
        using var pdf = PdfDocument.Open(output);
        // Every copy gives the text back, as it was typed, as pdf.extract-text writes it (Arial draws the space and the
        // no-break space with one glyph, which pdfium may read back as either).
        var text = PdfRenderWorker.PageText(pdf.GetText(0));
        Assert.Contains(Text, text.ReplaceLineEndings(" "), StringComparison.Ordinal);
        var page = pdf.Render(0, 72);
        var (width, height) = (page.Width, page.Height);
        // Light grey (30% of mid grey on white) at the middle of the page, and all over it: in each quarter.
        var grey = Greys(page, 0, 0, width, height);
        Assert.True(grey.Count > 500, $"{grey.Count} grey pixels");
        // Never darker than the grey at 30%: only its anti-aliased edges are lighter.
        Assert.All(grey, value => Assert.InRange(value, 210, 249));
        Assert.Contains(Greys(page, (width / 2) - 60, (height / 2) - 60, 120, 120), value => value < 235);
        foreach (var (x, y) in new[] { (0, 0), (width / 2, 0), (0, height / 2), (width / 2, height / 2) })
            Assert.True(Greys(page, x, y, width / 2, height / 2).Count > 50, $"no watermark in the quarter at {x}, {y}");
    }

    [Fact]
    public async Task Without_text_each_file_fails_at_once_and_says_why()
    {
        var input = _dir.Combine("a.pdf");
        WriteWhitePage(input, 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Op("").RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfWatermark.NoTextMessage, ex.Message);
        Assert.Equal(["a.pdf"], FileNames(_dir.Path));
    }

    [Theory]
    [InlineData("pdf.watermark")]
    [InlineData("pdf.page-numbers")]
    [InlineData("pdf.n-up-4")]
    public async Task A_page_larger_than_pdf_allows_is_refused_before_it_is_measured(string tool)
    {
        var input = _dir.Combine("huge.pdf");
        ToolPdf.Write(input,
        [
            ToolPdf.Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            ToolPdf.Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            ToolPdf.Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100000000000.0 100000000000.0] /Resources << >> >>"),
        ]);
        var op = PdfTools.Create(tool, tool == "pdf.watermark" ? new Dictionary<string, string> { [PdfTools.Text] = Text } : [], Qpdf.NextToApp, TestPdf.Worker);

        var ex = await Assert.ThrowsAsync<PdfException>(() => op.RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.TooLarge, ex.Error);
        Assert.Equal("Page 1 of this PDF is larger than PDF allows (200 inches on a side), so Flevert cannot change it.", ex.Message);
        Assert.Equal(["huge.pdf"], FileNames(_dir.Path));
    }

    [Theory]
    [InlineData("  Bản sao  ", "Bản sao")]
    [InlineData("Bản sao", "Bản sao")] // decomposed, as some keyboards type it
    public void The_text_is_trimmed_and_composed(string typed, string written) => Assert.Equal(written, Op(typed).Text);

    [Theory]
    [InlineData("one\nline")]
    [InlineData("tab\there")]
    public void A_text_that_is_not_one_line_of_characters_is_refused(string typed) =>
        Assert.Throws<ArgumentException>(() => Op(typed));

    [Fact]
    public void A_lone_surrogate_is_no_character_and_is_refused() =>
        Assert.Throws<ArgumentException>(() => Op("Bản sao " + '\uD800'));

    [Fact]
    public void At_most_a_hundred_characters_as_people_count_them()
    {
        Assert.Equal(100, Op(new string('ệ', 100)).Text.Length);
        Assert.Equal(200, Op(string.Concat(Enumerable.Repeat("😀", 100))).Text.Length);
        Assert.Throws<ArgumentException>(() => Op(new string('a', 101)));
    }

    private static void WriteWhitePage(string path, int rotate) => ToolPdf.Write(path,
    [
        ToolPdf.Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
        ToolPdf.Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
        ToolPdf.Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Rotate {rotate} /Resources << >> >>"),
    ]);

    // The grey values (R = G = B, not white) in a box of a page drawn on white.
    private static List<int> Greys(PdfBitmap page, int left, int top, int width, int height)
    {
        var values = new List<int>();
        for (var y = top; y < Math.Min(top + height, page.Height); y++)
        {
            for (var x = left; x < Math.Min(left + width, page.Width); x++)
            {
                var (r, g, b, _) = Pixel(page, x, y);
                if (r < 250 && r == g && g == b)
                    values.Add(r);
            }
        }

        return values;
    }
}

/// <summary>docs/specs/pdf.md, v0.2.2, success criterion 4: <c>2 / 4 pages per sheet</c> and <c>Fit pages to A4</c>.</summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfSheetsTests : IDisposable
{
    private const double A4Short = 595.28, A4Long = 841.89;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void They_are_not_in_the_right_click_menu()
    {
        Assert.Equal(("pdf.n-up-2", ""), (new PdfNUp(2, Qpdf.NextToApp).Id, new PdfNUp(2, Qpdf.NextToApp).MenuPath));
        Assert.Equal("pdf.n-up-4", new PdfNUp(4, Qpdf.NextToApp).Id);
        Assert.Equal(("pdf.fit-a4", ""), (new PdfFitA4(Qpdf.NextToApp).Id, new PdfFitA4(Qpdf.NextToApp).MenuPath));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfNUp(3, Qpdf.NextToApp));
    }

    [Theory]
    [InlineData(2, 5, 3, true)]
    [InlineData(4, 5, 2, false)]
    [InlineData(2, 1, 1, true)]
    public async Task Pages_go_in_reading_order_on_a4_sheets(int perSheet, int pages, int sheets, bool landscape)
    {
        var input = _dir.Combine("Bài giảng.pdf");
        WriteNumberedColours(input, pages);

        var output = await new PdfNUp(perSheet, Qpdf.NextToApp, TestPdf.Worker).RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine($"Bài giảng ({perSheet} per sheet).pdf"), output);
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(sheets, pdf.PageCount);
        for (var sheet = 0; sheet < sheets; sheet++)
            AssertA4(pdf.GetPageSize(sheet), landscape);
        // The first sheet: each slot shows its page's colour at its middle, in reading order.
        var first = pdf.Render(0, 36);
        var (w, h) = (first.Width, first.Height);
        var slots = perSheet == 2
            ? new[] { (w / 4, h / 2), (3 * w / 4, h / 2) }
            : [(w / 4, h / 4), (3 * w / 4, h / 4), (w / 4, 3 * h / 4), (3 * w / 4, 3 * h / 4)];
        for (var slot = 0; slot < Math.Min(perSheet, pages); slot++)
            Assert.Equal(Colour(slot), Pixel(first, slots[slot].Item1, slots[slot].Item2));
    }

    [Fact]
    public async Task Letter_a3_and_odd_pages_fit_a4_each_in_its_orientation_and_an_a4_page_stays_as_it_is()
    {
        var input = _dir.Combine("khổ lạ.pdf");
        // Letter, A3, a wide odd size, A4.
        ToolPdf.WriteHalves(input, (612, 792, 0), (842, 1191, 0), (1000, 300, 0), (595.28, 841.89, 0));

        var output = await new PdfFitA4(Qpdf.NextToApp, TestPdf.Worker).RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("khổ lạ (A4).pdf"), output);
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(4, pdf.PageCount);
        AssertA4(pdf.GetPageSize(0), landscape: false);
        AssertA4(pdf.GetPageSize(1), landscape: false);
        AssertA4(pdf.GetPageSize(2), landscape: true);
        AssertA4(pdf.GetPageSize(3), landscape: false);
        // Scaled whole and centred: red on the left of the middle, blue on the right, white above and below the wide one.
        for (var page = 0; page < 4; page++)
        {
            var bitmap = pdf.Render(page, 36);
            Assert.Equal((255, 0, 0, 255), Pixel(bitmap, (bitmap.Width / 2) - 5, bitmap.Height / 2));
            Assert.Equal((0, 0, 255, 255), Pixel(bitmap, (bitmap.Width / 2) + 5, bitmap.Height / 2));
        }

        var wide = pdf.Render(2, 36);
        Assert.Equal((255, 255, 255, 255), Pixel(wide, wide.Width / 4, 5));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public async Task A_turned_page_is_fitted_as_it_is_shown(int rotate)
    {
        // 400 x 600 turned a quarter: shown 600 wide, 400 high, its red half at the top (90) or the bottom (270).
        var input = _dir.Combine("xoay.pdf");
        ToolPdf.WriteHalves(input, (400, 600, rotate));

        var output = await new PdfFitA4(Qpdf.NextToApp, TestPdf.Worker).RunAsync(input, CancellationToken.None);

        using var pdf = PdfDocument.Open(output);
        AssertA4(pdf.GetPageSize(0), landscape: true);
        var bitmap = pdf.Render(0, 36);
        var (top, bottom) = (Pixel(bitmap, bitmap.Width / 2, bitmap.Height / 4), Pixel(bitmap, bitmap.Width / 2, 3 * bitmap.Height / 4));
        Assert.Equal(rotate == 90 ? ((255, 0, 0, 255), (0, 0, 255, 255)) : ((0, 0, 255, 255), (255, 0, 0, 255)), (top, bottom));
    }

    [Fact]
    public async Task A_pdf_of_a4_pages_only_is_skipped()
    {
        var input = _dir.Combine("a4.pdf");
        ToolPdf.WriteHalves(input, (595, 842, 0), (842, 595, 0), (595.28, 841.89, 90));

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => new PdfFitA4(Qpdf.NextToApp, TestPdf.Worker).RunAsync(input, CancellationToken.None));

        Assert.Equal("Every page of this PDF is already A4.", ex.Message);
        Assert.Equal(["a4.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task A_filled_form_shows_its_answers_on_the_sheet()
    {
        var input = _dir.Combine("form.pdf");
        ToolPdf.WriteFilledForm(input, "Nguyen Van A");

        var output = await new PdfNUp(2, Qpdf.NextToApp, TestPdf.Worker).RunAsync(input, CancellationToken.None);

        using var pdf = PdfDocument.Open(output);
        Assert.Contains("Nguyen Van A", pdf.GetText(0), StringComparison.Ordinal);
    }

    private static void AssertA4(PdfPageSize size, bool landscape)
    {
        var (width, height) = landscape ? (A4Long, A4Short) : (A4Short, A4Long);
        Assert.InRange(size.Width, width - 1, width + 1);
        Assert.InRange(size.Height, height - 1, height + 1);
    }

    private static (byte, byte, byte, byte) Colour(int page) => (page % 3) switch
    {
        0 => (255, 0, 0, 255),
        1 => (0, 128, 0, 255),
        _ => (0, 0, 255, 255),
    };

    // Letter pages, each filled with its colour: red, green, blue, red…
    private static void WriteNumberedColours(string path, int pages)
    {
        var objects = new List<byte[]>
        {
            ToolPdf.Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            ToolPdf.Ascii($"<< /Type /Pages /Count {pages} /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => (3 + (2 * i)) + " 0 R"))}] >>"),
        };
        for (var i = 0; i < pages; i++)
        {
            var (r, g, b, _) = Colour(i);
            objects.Add(ToolPdf.Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents {4 + (2 * i)} 0 R >>"));
            objects.Add(ToolPdf.Stream("", ToolPdf.Ascii(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{r / 255.0:0.###} {g / 255.0:0.###} {b / 255.0:0.###} rg 0 0 612 792 re f"))));
        }

        ToolPdf.Write(path, objects);
    }
}

/// <summary>docs/specs/pdf.md, v0.2.2, success criterion 7: <c>Extract images</c>.</summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfExtractImagesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PdfExtractImages Op() => new(TestPdf.Worker);

    [Fact]
    public void It_is_not_in_the_right_click_menu()
    {
        var op = Op();

        Assert.Equal(("pdf.extract-images", "", 1), (op.Id, op.MenuPath, op.MaxParallel));
    }

    [Fact]
    public async Task A_jpeg_comes_out_byte_for_byte_once_another_image_as_a_png_and_icons_not_at_all()
    {
        var jpeg = TestJpeg.Make(96, 64);
        var input = _dir.Combine("Ảnh hồ sơ.pdf");
        ToolPdf.WriteImages(input, jpeg, 96, 64);

        var folder = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Ảnh hồ sơ (images)"), folder);
        Assert.Equal(["Ảnh hồ sơ 001.jpg", "Ảnh hồ sơ 002.png"], FileNames(folder));
        Assert.Equal(jpeg, await File.ReadAllBytesAsync(Path.Combine(folder, "Ảnh hồ sơ 001.jpg")));
        using var png = new ImageMagick.MagickImage(Path.Combine(folder, "Ảnh hồ sơ 002.png"));
        Assert.Equal((40u, 36u), (png.Width, png.Height));
        var pixel = png.GetPixels().GetPixel(20, 15).ToColor()!;
        Assert.Equal((0, 160, 0), (pixel.R, pixel.G, pixel.B));
    }

    [Fact]
    public async Task A_pdf_without_images_is_skipped()
    {
        var input = _dir.Combine("text.pdf");
        File.Copy(Fixture("three-colours.pdf"), input);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal("This PDF has no images to extract.", ex.Message);
        Assert.Equal(["text.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public async Task Progress_counts_pages_then_images()
    {
        var input = _dir.Combine("p.pdf");
        ToolPdf.WriteImages(input, TestJpeg.Make(96, 64), 96, 64);
        var reports = new List<FileProgress>();

        await Op().RunAsync(input, new Sync(reports.Add), CancellationToken.None);

        Assert.Equal(
            [new("page", 1, 3), new("page", 2, 3), new("page", 3, 3), new("image", 1, 2), new("image", 2, 2)],
            reports);
    }

    private sealed class Sync(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }
}
