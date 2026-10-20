using System.Security.Cryptography;

namespace Flevert.Pdf.Tests;

public class PdfDocumentTests
{
    private static readonly (byte R, byte G, byte B, byte A) Red = (255, 0, 0, 255);
    private static readonly (byte R, byte G, byte B, byte A) Green = (0, 255, 0, 255);
    private static readonly (byte R, byte G, byte B, byte A) Blue = (0, 0, 255, 255);

    [Fact]
    public void Reads_the_page_count_and_each_page_size_in_points()
    {
        using var pdf = PdfDocument.Open(TestPdf.Fixture("three-colours.pdf"));

        Assert.Equal(3, pdf.PageCount);
        Assert.Equal(new PdfPageSize(595, 842), pdf.GetPageSize(0));
        Assert.Equal(new PdfPageSize(612, 792), pdf.GetPageSize(1));
        Assert.Equal(new PdfPageSize(400, 300), pdf.GetPageSize(2));
    }

    [Fact]
    public void Renders_each_page_in_its_own_colour_at_the_size_its_dpi_gives()
    {
        using var pdf = PdfDocument.Open(TestPdf.Fixture("three-colours.pdf"));
        (int Width, int Height, (byte, byte, byte, byte) Colour)[] expected = [(595, 842, Red), (612, 792, Green), (400, 300, Blue)];

        for (var page = 0; page < expected.Length; page++)
        {
            var bitmap = pdf.Render(page, 72);

            Assert.Equal((expected[page].Width, expected[page].Height), (bitmap.Width, bitmap.Height));
            Assert.Equal(bitmap.Width * 4, bitmap.Stride);
            Assert.Equal(bitmap.Stride * bitmap.Height, bitmap.Bgra.Length);
            Assert.Equal(72, bitmap.Dpi);
            foreach (var (x, y) in SamplePoints(bitmap))
                Assert.Equal(expected[page].Colour, TestPdf.Pixel(bitmap, x, y));
        }
    }

    [Fact]
    public void Scales_the_page_by_the_dpi()
    {
        using var pdf = PdfDocument.Open(TestPdf.Fixture("three-colours.pdf"));

        // 595 x 842 pt at 150 DPI is 1239.6 x 1754.2 px.
        Assert.Equal(new PdfRenderSize(1240, 1754, 150), pdf.GetRenderSize(0, 150));
        var bitmap = pdf.Render(0, 150);
        Assert.Equal((1240, 1754), (bitmap.Width, bitmap.Height));
        Assert.Equal(Red, TestPdf.Pixel(bitmap, 1239, 1753));
    }

    [Fact]
    public void Lowers_the_dpi_of_a_page_that_would_pass_10000_px_on_a_side()
    {
        using var pdf = PdfDocument.Open(TestPdf.Fixture("huge-page.pdf"));

        // 200 x 2 inches: 30000 px wide at 150 DPI, so 50 DPI gives the widest side 10000 px.
        var size = pdf.GetRenderSize(0, 150);
        Assert.Equal(new PdfRenderSize(10_000, 100, 50), size);
        Assert.Equal(10_000L * 100 * 4, size.Bytes);

        var bitmap = pdf.Render(0, 150);
        Assert.Equal((10_000, 100, 50.0), (bitmap.Width, bitmap.Height, bitmap.Dpi));
        Assert.Equal(Red, TestPdf.Pixel(bitmap, 2_500, 50));
        Assert.Equal(Blue, TestPdf.Pixel(bitmap, 7_500, 50));
    }

    [Fact]
    public void The_limits_are_the_ones_in_the_spec()
    {
        Assert.Equal(2_000, PdfDocument.MaxPages);
        Assert.Equal(2L * 1024 * 1024 * 1024, PdfDocument.MaxFileBytes);
        Assert.Equal(10_000, PdfDocument.MaxRenderPixels);
    }

    [Fact]
    public void Opens_a_pdf_whose_folder_and_name_are_not_ascii()
    {
        using var dir = new TempDir();
        var path = dir.Combine("Hóa đơn tháng 10 — 中文.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), path);

        using var pdf = PdfDocument.Open(path);

        Assert.Equal(3, pdf.PageCount);
        Assert.Equal(Red, TestPdf.Pixel(pdf.Render(0, 36), 10, 10));
    }

    [Fact]
    public void Opens_a_pdf_at_a_long_path_given_with_the_extended_prefix()
    {
        using var dir = new TempDir();
        var folder = @"\\?\" + dir.Path;
        while (folder.Length < 320)
            folder = Path.Combine(folder, "thư mục con rất dài để vượt quá giới hạn MAX_PATH");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Ảnh Đà Lạt.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), path);

        using var pdf = PdfDocument.Open(path);

        Assert.True(path.Length > 300);
        Assert.Equal(3, pdf.PageCount);
        Assert.Equal(Green, TestPdf.Pixel(pdf.Render(1, 36), 10, 10));
    }

    [Fact]
    public void A_password_protected_pdf_is_refused_with_a_message_naming_the_fix()
    {
        var ex = Assert.Throws<PdfException>(() => PdfDocument.Open(TestPdf.Fixture("password-protected.pdf")));

        Assert.Equal(PdfError.PasswordRequired, ex.Error);
        Assert.Equal("This PDF needs a password: use Remove password first", ex.Message);
    }

    [Fact]
    public void Opens_a_pdf_of_exactly_2000_pages()
    {
        using var dir = new TempDir();
        var path = dir.Combine("2000 pages.pdf");
        TestPdf.WriteBlank(path, 2_000);

        using var pdf = PdfDocument.Open(path);

        Assert.Equal(2_000, pdf.PageCount);
    }

    [Fact]
    public void Refuses_a_pdf_of_more_than_2000_pages()
    {
        using var dir = new TempDir();
        var path = dir.Combine("2001 pages.pdf");
        TestPdf.WriteBlank(path, 2_001);

        var ex = Assert.Throws<PdfException>(() => PdfDocument.Open(path));

        Assert.Equal(PdfError.TooManyPages, ex.Error);
        Assert.Contains("2001", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_file_over_the_size_limit_before_parsing_it()
    {
        var path = TestPdf.Fixture("three-colours.pdf");
        var limits = PdfLimits.Default with { MaxFileBytes = new FileInfo(path).Length - 1 };

        var ex = Assert.Throws<PdfException>(() => PdfDocument.Open(path, limits));

        Assert.Equal(PdfError.TooLarge, ex.Error);
        using var atTheLimit = PdfDocument.Open(path, limits with { MaxFileBytes = new FileInfo(path).Length });
        Assert.Equal(3, atTheLimit.PageCount);
    }

    [Theory]
    [InlineData("This is a text file someone renamed to .pdf.\r\n")]
    [InlineData("")]
    [InlineData("%PDF-1.7\n1 0 obj\n<< /Type /Catalog")]
    public void A_file_that_is_not_a_pdf_gives_a_clean_error(string content)
    {
        using var dir = new TempDir();
        var path = dir.File("notes.pdf", content);

        var ex = Assert.Throws<PdfException>(() => PdfDocument.Open(path));

        Assert.Equal(PdfError.NotPdf, ex.Error);
        Assert.Equal("This file is not a PDF, or it is damaged.", ex.Message);
    }

    [Fact]
    public void A_missing_file_gives_the_usual_file_error()
    {
        using var dir = new TempDir();

        Assert.Throws<FileNotFoundException>(() => PdfDocument.Open(dir.Combine("gone.pdf")));
    }

    [Fact]
    public void Rejects_a_page_number_or_dpi_out_of_range()
    {
        using var pdf = PdfDocument.Open(TestPdf.Fixture("three-colours.pdf"));

        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.GetPageSize(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Render(-1, 72));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Render(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Render(0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.GetRenderSize(0, double.PositiveInfinity));
    }

    [Fact]
    public void A_closed_document_cannot_be_used()
    {
        var pdf = PdfDocument.Open(TestPdf.Fixture("three-colours.pdf"));
        pdf.Dispose();
        pdf.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pdf.PageCount);
        Assert.Throws<ObjectDisposedException>(() => pdf.Render(0, 72));
    }

    [Fact]
    public void The_file_can_be_read_but_not_replaced_while_the_document_is_open()
    {
        using var dir = new TempDir();
        var path = dir.Combine("open.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), path);

        using (PdfDocument.Open(path))
        {
            Assert.NotEmpty(File.ReadAllBytes(path));
            Assert.Throws<IOException>(() => File.WriteAllText(path, "changed under pdfium"));
        }

        File.Delete(path);
    }

    [Fact]
    public async Task Eight_threads_rendering_at_once_get_the_same_pixels_as_one()
    {
        var fixture = TestPdf.Fixture("three-colours.pdf");
        string[] reference;
        using (var pdf = PdfDocument.Open(fixture))
            reference = HashPages(pdf);
        using var shared = PdfDocument.Open(fixture);
        using var start = new Barrier(8);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(thread => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            var hashes = new List<string[]>();
            for (var round = 0; round < 3; round++)
            {
                // Half the threads open their own document, half share one: both must be safe.
                if (thread % 2 == 0)
                {
                    using var own = PdfDocument.Open(fixture);
                    hashes.Add(HashPages(own));
                }
                else
                {
                    hashes.Add(HashPages(shared));
                }
            }

            return hashes;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)));

        foreach (var hashes in results.SelectMany(r => r))
            Assert.Equal(reference, hashes);
    }

    private static string[] HashPages(PdfDocument pdf) =>
        Enumerable.Range(0, pdf.PageCount).Select(page => Convert.ToHexString(SHA256.HashData(pdf.Render(page, 100).Bgra.Span))).ToArray();

    // Corners (one pixel in, past anti-aliased edges) and the centre.
    private static (int X, int Y)[] SamplePoints(PdfBitmap bitmap) =>
        [(1, 1), (bitmap.Width - 2, 1), (1, bitmap.Height - 2), (bitmap.Width - 2, bitmap.Height - 2), (bitmap.Width / 2, bitmap.Height / 2)];
}
