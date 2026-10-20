using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ImageMagick;
using Flevert.Core;
using Flevert.Image;
using Xunit.Abstractions;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/pdf.md, <c>pdf.to-jpg</c> and <c>pdf.to-png</c>: a folder of one image per page at 150 DPI.</summary>
[Collection(WorkerProcesses.Name)]
public class PdfToImagesTests(ITestOutputHelper output)
{
    private static readonly (byte R, byte G, byte B) Red = (255, 0, 0);
    private static readonly (byte R, byte G, byte B) Green = (0, 255, 0);
    private static readonly (byte R, byte G, byte B) Blue = (0, 0, 255);

    // A4, Letter and 400 x 300 pt at 150 DPI.
    private static readonly (int Width, int Height, (byte, byte, byte) Colour)[] ThreeColoursAt150 =
        [(1240, 1754, Red), (1275, 1650, Green), (833, 625, Blue)];

    // The operations as the product runs them, with the worker of the flevert.exe this solution built: every test here
    // goes through the full parent path (folder, worker process, progress lines, the pages it wrote).
    private static PdfToImages Jpg(double dpi = PdfToImages.Dpi) => new(PdfPageImageFormat.Jpeg, dpi, TestPdf.Worker);

    private static PdfToImages Png(double dpi = PdfToImages.Dpi) => new(PdfPageImageFormat.Png, dpi, TestPdf.Worker);

    [Fact]
    public void The_operations_are_the_ones_in_the_spec()
    {
        var jpg = new PdfToImages(PdfPageImageFormat.Jpeg);
        var png = new PdfToImages(PdfPageImageFormat.Png);

        Assert.Equal(("pdf.to-jpg", "Convert/JPG pages"), (jpg.Id, jpg.MenuPath));
        Assert.Equal(("pdf.to-png", "Convert/PNG pages"), (png.Id, png.MenuPath));
        Assert.Equal([".pdf"], jpg.Extensions);
        Assert.Equal([".pdf"], png.Extensions);
        // One worker at a time, each with its own memory cap (ADR-0008).
        Assert.Equal(1, jpg.MaxParallel);
        Assert.Equal(1, png.MaxParallel);
        Assert.Equal(150, PdfToImages.Dpi);
    }

    [Fact]
    public void The_render_operations_list_offers_both_and_the_text()
    {
        Assert.Equal(["pdf.to-jpg", "pdf.to-png", "pdf.extract-text"], PdfRenderOperations.All.Select(op => op.Id));
    }

    [Fact]
    public async Task To_jpg_writes_one_jpeg_per_page_at_150_dpi_in_a_pages_folder()
    {
        using var dir = new TempDir();
        var input = CopyFixture(dir, "three-colours.pdf", "Báo cáo quý.pdf");

        var folder = await Jpg().RunAsync(input, CancellationToken.None);

        Assert.Equal(dir.Combine("Báo cáo quý (pages)"), folder);
        Assert.Equal(["Báo cáo quý 001.jpg", "Báo cáo quý 002.jpg", "Báo cáo quý 003.jpg"], Names(folder));
        for (var page = 0; page < 3; page++)
        {
            using var images = ImageFile.Read(Path.Combine(folder, Names(folder)[page]));
            var image = images[0];
            Assert.Equal(MagickFormat.Jpeg, image.Format);
            // Spec: quality 90 with 4:2:0 chroma (ImageMagick would keep 4:4:4 from quality 90 up).
            Assert.Equal(90u, image.Quality);
            Assert.Equal("2x2,1x1,1x1", image.GetAttribute("jpeg:sampling-factor"));
            Assert.Equal((ThreeColoursAt150[page].Width, ThreeColoursAt150[page].Height), ((int)image.Width, (int)image.Height));
            Assert.Equal(150, image.Density.X, 0.5);
            Assert.Equal(DensityUnit.PixelsPerInch, image.Density.Units);
            foreach (var (x, y) in SamplePoints(image))
                AssertClose(ThreeColoursAt150[page].Colour, ColourAt(image, x, y), tolerance: 8);
        }
    }

    [Fact]
    public async Task To_png_writes_exact_colours_without_an_alpha_channel()
    {
        using var dir = new TempDir();
        var input = CopyFixture(dir, "three-colours.pdf", "colours.pdf");

        var folder = await Png().RunAsync(input, CancellationToken.None);

        Assert.Equal(["colours 001.png", "colours 002.png", "colours 003.png"], Names(folder));
        for (var page = 0; page < 3; page++)
        {
            using var images = ImageFile.Read(Path.Combine(folder, Names(folder)[page]));
            var image = images[0];
            Assert.Equal(MagickFormat.Png, image.Format);
            Assert.False(image.HasAlpha);
            Assert.Equal((ThreeColoursAt150[page].Width, ThreeColoursAt150[page].Height), ((int)image.Width, (int)image.Height));
            foreach (var (x, y) in SamplePoints(image))
                Assert.Equal(ThreeColoursAt150[page].Colour, ColourAt(image, x, y));
        }
    }

    [Fact]
    public async Task A_huge_page_renders_within_10000_px_at_a_lower_dpi()
    {
        using var dir = new TempDir();
        var input = CopyFixture(dir, "huge-page.pdf", "banner.pdf");

        var folder = await Png().RunAsync(input, CancellationToken.None);

        using var images = ImageFile.Read(Path.Combine(folder, "banner 001.png"));
        var image = images[0];
        // 200 x 2 inches: 30000 px wide at 150 DPI, so 50 DPI.
        Assert.Equal((10_000u, 100u), (image.Width, image.Height));
        // PNG stores pixels per metre; ImageMagick reads it back per centimetre.
        Assert.Equal(50, image.Density.ChangeUnits(DensityUnit.PixelsPerInch).X, 0.5);
        Assert.Equal(Red, ColourAt(image, 2_500, 50));
        Assert.Equal(Blue, ColourAt(image, 7_500, 50));
    }

    [Theory]
    [InlineData(PdfPageImageFormat.Jpeg)]
    [InlineData(PdfPageImageFormat.Png)]
    public async Task Pages_of_more_than_4000_megapixels_in_all_are_refused_before_any_is_drawn(PdfPageImageFormat format)
    {
        // 41 pages that each render at 10 000 x 10 000 px: 4 100 megapixels, each page within the limit of one.
        using var dir = new TempDir();
        var input = dir.Combine("posters.pdf");
        TestPdf.WriteSquarePages(input, 41, 14_400);
        var timer = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<PdfException>(() => new PdfToImages(format, PdfToImages.Dpi, TestPdf.Worker).RunAsync(input, CancellationToken.None));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}");
        Assert.Equal(PdfError.TooLarge, ex.Error);
        Assert.Equal("This PDF is too large to convert to pictures: its pages together would be more than 4000 megapixels.", ex.Message);
        Assert.Equal(["posters.pdf"], Directory.GetFileSystemEntries(dir.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(1_000_000L, PdfPageImageFormat.Jpeg, 500_000L, null)]
    [InlineData(1_000_000L, PdfPageImageFormat.Jpeg, 499_999L, "about 1 MB, and 1 MB is free")]
    [InlineData(1_000_000_000L, PdfPageImageFormat.Png, 2_000_000_000L, "about 2.8 GB, and 1.9 GB is free")]
    [InlineData(4_000_000_000L, PdfPageImageFormat.Png, null, null)]
    public void The_pictures_need_half_a_byte_a_pixel_as_jpeg_and_three_as_png(long pixels, PdfPageImageFormat format, long? free, string? lacking)
    {
        var problem = PdfRenderBudget.Check(pixels, format, free);

        if (lacking is null)
            Assert.Null(problem);
        else
            Assert.Equal($"There is not enough free space for the pictures of this PDF: they need {lacking}.", problem);
    }

    [Fact]
    public void Past_4000_megapixels_nothing_is_converted_whatever_the_disk()
    {
        var ex = Assert.Throws<PdfException>(() => PdfRenderBudget.Check(4_000_000_001L, PdfPageImageFormat.Jpeg, long.MaxValue));

        Assert.Equal(PdfError.TooLarge, ex.Error);
    }

    [Fact]
    public void The_free_space_of_a_folder_is_what_windows_says()
    {
        using var dir = new TempDir();
        var free = PdfRenderBudget.FreeBytes(dir.Path);

        Assert.NotNull(free);
        Assert.InRange(free.Value, 1, new DriveInfo(Path.GetPathRoot(dir.Path)!).TotalSize);
    }

    [Fact]
    public async Task Numbers_have_as_many_digits_as_the_page_count_needs()
    {
        using var dir = new TempDir();
        var input = dir.Combine("long.pdf");
        TestPdf.WriteBlank(input, 1_000);

        // 2 DPI keeps 1 000 Letter pages tiny (17 x 22 px); the naming is the same at any DPI.
        var folder = await Jpg(2).RunAsync(input, CancellationToken.None);

        var names = Names(folder);
        Assert.Equal(1_000, names.Length);
        Assert.Equal("long 0001.jpg", names[0]);
        Assert.Equal("long 0999.jpg", names[998]);
        Assert.Equal("long 1000.jpg", names[999]);
    }

    [Fact]
    public async Task Reports_progress_once_per_page_in_order()
    {
        using var dir = new TempDir();
        var input = CopyFixture(dir, "three-colours.pdf", "colours.pdf");
        var progress = new Recorder();

        await Jpg().RunAsync(input, progress, CancellationToken.None);

        Assert.Equal([("page", 1, 3), ("page", 2, 3), ("page", 3, 3)], progress.Reports.Select(r => (r.Unit, r.Position, r.Total)));
    }

    [Fact]
    public async Task Cancelling_midway_leaves_nothing_beside_the_input()
    {
        using var dir = new TempDir();
        var input = dir.Combine("many.pdf");
        TestPdf.WriteBlank(input, 20);
        using var cts = new CancellationTokenSource();
        var progress = new Recorder(report =>
        {
            if (report.Position == 5)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Png().RunAsync(input, progress, cts.Token));

        Assert.Equal(["many.pdf"], Names(dir.Path));
        Assert.Equal(5, progress.Reports.Count);
    }

    [Fact]
    public async Task A_password_protected_pdf_fails_with_the_message_naming_the_fix_and_leaves_nothing()
    {
        using var dir = new TempDir();
        var input = CopyFixture(dir, "password-protected.pdf", "secret.pdf");

        var ex = await Assert.ThrowsAsync<PdfException>(
            () => Jpg().RunAsync(input, CancellationToken.None));

        Assert.Equal("This PDF needs a password: use Remove password first", ex.Message);
        Assert.Equal(["secret.pdf"], Names(dir.Path));
    }

    [Fact]
    public async Task A_page_pdfium_cannot_read_fails_the_whole_file_naming_the_page()
    {
        using var dir = new TempDir();
        var input = dir.Combine("damaged.pdf");
        // Page 2 points at an object the file does not have.
        WritePages(input, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>", null, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");

        var ex = await Assert.ThrowsAsync<PdfException>(
            () => Png().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.DamagedPage, ex.Error);
        Assert.StartsWith("Page 2 of this PDF", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["damaged.pdf"], Names(dir.Path));
    }

    [Fact]
    public async Task A_pdf_without_pages_fails_and_leaves_nothing()
    {
        using var dir = new TempDir();
        var input = dir.Combine("empty.pdf");
        TestPdf.WriteBlank(input, 0);

        var ex = await Assert.ThrowsAsync<PdfException>(
            () => Png().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.NoPages, ex.Error);
        Assert.Equal("This PDF has no pages.", ex.Message);
        Assert.Equal(["empty.pdf"], Names(dir.Path));
    }

    [Fact]
    public async Task An_existing_pages_folder_is_kept_and_the_new_one_gets_a_number()
    {
        using var dir = new TempDir();
        var input = CopyFixture(dir, "three-colours.pdf", "colours.pdf");
        Directory.CreateDirectory(dir.Combine("colours (pages)"));
        File.WriteAllText(dir.Combine(@"colours (pages)\mine.txt"), "keep me");

        var folder = await Jpg().RunAsync(input, CancellationToken.None);

        Assert.Equal(dir.Combine("colours (pages) (1)"), folder);
        Assert.Equal(["mine.txt"], Names(dir.Combine("colours (pages)")));
        Assert.Equal(3, Names(folder).Length);
    }

    [Fact]
    public async Task The_original_is_not_changed()
    {
        using var dir = new TempDir();
        var input = CopyFixture(dir, "three-colours.pdf", "colours.pdf");
        var before = SHA256.HashData(File.ReadAllBytes(input));
        var written = File.GetLastWriteTimeUtc(input);

        await Png().RunAsync(input, CancellationToken.None);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(input)));
        Assert.Equal(written, File.GetLastWriteTimeUtc(input));
    }

    [Fact]
    public async Task A_long_name_is_shortened_so_every_page_name_fits()
    {
        using var dir = new TempDir();
        // 251 + ".pdf" is the longest name a file can have; " 001.jpg" pushes a page name past 255, so it must be cut.
        var stem = string.Concat(Enumerable.Repeat("Ảnh chụp ", 30))[..251];
        var input = dir.Combine("x.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);
        var longInput = @"\\?\" + dir.Combine(stem + ".pdf");
        File.Move(input, longInput);

        var folder = await Jpg(10).RunAsync(longInput, CancellationToken.None);

        var names = Names(folder);
        Assert.Equal(3, names.Length);
        Assert.All(names, name => Assert.Equal(255, name.Length));
        Assert.EndsWith(" 001.jpg", names[0], StringComparison.Ordinal);
        Assert.StartsWith(stem[..200], names[0], StringComparison.Ordinal);
    }

    // Stems of 248 to 251 characters make "<stem> (pages)" longer than 255: the stem is shortened, not the suffix.
    [Fact]
    public async Task A_long_name_keeps_the_pages_suffix_on_the_folder()
    {
        using var dir = new TempDir();
        var stem = string.Concat(Enumerable.Repeat("Ảnh chụp ", 30))[..249];
        var input = dir.Combine("x.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);
        var longInput = @"\\?\" + dir.Combine(stem + ".pdf");
        File.Move(input, longInput);

        var folder = await Jpg(10).RunAsync(longInput, CancellationToken.None);

        Assert.EndsWith(" (pages)", folder, StringComparison.Ordinal);
        Assert.Equal(255, Path.GetFileName(folder).Length);
        Assert.Equal(3, Names(folder).Length);
    }

    [Theory]
    [InlineData(PdfPageImageFormat.Jpeg, "report 200.jpg")]
    [InlineData(PdfPageImageFormat.Png, "report 200.png")]
    public async Task Two_hundred_pages_finish_in_reasonable_time(PdfPageImageFormat format, string last)
    {
        using var dir = new TempDir();
        var input = dir.Combine("report.pdf");
        TestPdf.WriteBlank(input, 200);

        var watch = Stopwatch.StartNew();
        var folder = await new PdfToImages(format, PdfToImages.Dpi, TestPdf.Worker).RunAsync(input, CancellationToken.None);
        watch.Stop();

        output.WriteLine($"200 Letter pages to {format} at 150 DPI: {watch.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(200, Names(folder).Length);
        Assert.Equal(last, Names(folder)[^1]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    private static string CopyFixture(TempDir dir, string fixture, string name)
    {
        var path = dir.Combine(name);
        File.Copy(TestPdf.Fixture(fixture), path);
        return path;
    }

    private static string[] Names(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    private static (byte R, byte G, byte B) ColourAt(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        var colour = pixels.GetPixel(x, y).ToColor()!;
        return (colour.R, colour.G, colour.B);
    }

    private static void AssertClose((byte R, byte G, byte B) expected, (byte R, byte G, byte B) actual, int tolerance)
    {
        Assert.True(
            Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected}, got {actual}");
    }

    // Corners (one pixel in) and the centre.
    private static (int X, int Y)[] SamplePoints(IMagickImage<byte> image)
    {
        var (w, h) = ((int)image.Width, (int)image.Height);
        return [(1, 1), (w - 2, 1), (1, h - 2), (w - 2, h - 2), (w / 2, h / 2)];
    }

    /// <summary>A PDF whose pages are the given objects; <c>null</c> makes that page point at an object that does not exist.</summary>
    private static void WritePages(string path, params string?[] pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
        var kids = new List<string>();
        foreach (var page in pages)
        {
            if (page is null)
            {
                kids.Add("99 0 R");
                continue;
            }

            objects.Add(page);
            kids.Add(objects.Count + " 0 R");
        }

        objects[1] = $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(" ", kids)}] >>";
        var body = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(body.Length);
            body.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = body.Length;
        body.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            body.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        body.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(body.ToString()));
    }

    /// <summary>Records reports synchronously, unlike <see cref="Progress{T}"/>, which posts them later.</summary>
    private sealed class Recorder(Action<FileProgress>? onReport = null) : IProgress<FileProgress>
    {
        private readonly List<FileProgress> _reports = [];

        public IReadOnlyList<FileProgress> Reports
        {
            get
            {
                lock (_reports)
                    return [.. _reports];
            }
        }

        public void Report(FileProgress value)
        {
            lock (_reports)
                _reports.Add(value);
            onReport?.Invoke(value);
        }
    }
}
