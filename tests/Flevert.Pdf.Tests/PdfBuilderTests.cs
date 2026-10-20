using System.Diagnostics;
using System.Text.Json;
using ImageMagick;
using Flevert.Image;

namespace Flevert.Pdf.Tests;

public sealed class PdfBuilderTests : IDisposable
{
    // 297 mm, the long side of A4, in points.
    private const double A4Long = 297 / 25.4 * 72;

    private readonly TempDir _dir = new();

    static PdfBuilderTests() => MagickSetup.EnsureInitialized();

    public void Dispose() => _dir.Dispose();

    private enum Colour
    {
        Red,
        Blue,
        Green,
        White,
        Other,
    }

    [Fact]
    public void Builds_a_pdf_from_a_turned_jpeg_a_large_jpeg_and_a_transparent_bitmap()
    {
        // Stored 320 x 240 with Orientation 6 (turn 90° clockwise to view): the stored top-left red block is viewed at
        // the top-right, the stored bottom-right blue block at the bottom-left.
        var turned = JpegSegments.Strip(TestJpeg.Make(320, 240, image => TestJpeg.SetOrientation(image, 6)));
        var large = JpegSegments.Strip(TestJpeg.Make(4000, 3000));
        var bitmap = TransparentBitmap(200, 100);

        var path = Build(builder =>
        {
            builder.AddJpegPage(turned, 150);
            builder.AddJpegPage(large); // no DPI known: 150
            builder.AddBitmapPage(bitmap, 200, 100, 72);
            Assert.Equal(3, builder.PageCount);
        });

        using var pdf = PdfDocument.Open(path);
        Assert.Equal(3, pdf.PageCount);

        // 240 x 320 px viewed, at 150 DPI.
        AssertSize(240 * 72 / 150.0, 320 * 72 / 150.0, pdf.GetPageSize(0));
        var first = pdf.Render(0, 150);
        Assert.Equal((240, 320), (first.Width, first.Height));
        Assert.Equal(Colour.Red, At(first, 0.75, 0.25));
        Assert.Equal(Colour.Blue, At(first, 0.25, 0.75));
        Assert.Equal(Colour.Other, At(first, 0.25, 0.25));

        // 4000 x 3000 px at 150 DPI is 1920 x 1440 pt, past A4's long side: scaled down to it, same proportions.
        AssertSize(A4Long, A4Long * 3 / 4, pdf.GetPageSize(1));
        var second = pdf.Render(1, 72);
        Assert.Equal(Colour.Red, At(second, 0.25, 0.25));
        Assert.Equal(Colour.Blue, At(second, 0.75, 0.75));

        // Opaque green, then red at half opacity, then fully transparent black: on white.
        AssertSize(200, 100, pdf.GetPageSize(2));
        var third = pdf.Render(2, 72);
        Assert.Equal(Colour.Green, At(third, 0.25, 0.5));
        var (r, g, b, _) = TestPdf.Pixel(third, 125, 50);
        Assert.Equal(255, r);
        Assert.InRange(g, 125, 130);
        Assert.InRange(b, 125, 130);
        Assert.Equal(Colour.White, At(third, 0.9, 0.5));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Each_exif_orientation_turns_the_page_as_imagemagick_turns_the_pixels(ushort orientation)
    {
        var original = TestJpeg.Make(96, 64, image => TestJpeg.SetOrientation(image, orientation));
        var jpeg = JpegSegments.Strip(original);
        Assert.Equal(orientation, jpeg.Orientation);
        using var upright = TestJpeg.Decode(original);
        upright.AutoOrient();

        var path = Build(builder => builder.AddJpegPage(jpeg, 150));

        using var pdf = PdfDocument.Open(path);
        var page = pdf.Render(0, 150);
        Assert.Equal((upright.Width, upright.Height), ((uint)page.Width, (uint)page.Height));
        using var pixels = upright.GetPixels();
        foreach (var (fx, fy) in new[] { (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75) })
        {
            var x = (int)(page.Width * fx);
            var y = (int)(page.Height * fy);
            var expected = pixels.GetPixel(x, y).ToColor()!;
            Assert.Equal(Classify(expected.R, expected.G, expected.B), At(page, fx, fy));
        }
    }

    [Fact]
    public void Embeds_the_stripped_jpeg_byte_for_byte_and_the_bitmap_compressed()
    {
        var jpeg = JpegSegments.Strip(TestJpeg.Make(160, 120));

        var path = Build(builder =>
        {
            builder.AddJpegPage(jpeg);
            builder.AddBitmapPage(TransparentBitmap(400, 300), 400, 300);
        });

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().IndexOf(jpeg.Bytes) >= 0, "the JPEG stream should be the stripped JPEG unchanged");
        var text = TestJpeg.Latin1(bytes);
        Assert.Contains("/DCTDecode", text, StringComparison.Ordinal);
        Assert.Contains("/FlateDecode", text, StringComparison.Ordinal);
        // 400 x 300 RGB is 360 000 bytes raw; three flat colours compress to almost nothing.
        Assert.True(bytes.Length < 50_000, $"the PDF is {bytes.Length} bytes");
        Assert.DoesNotContain("/SMask", text, StringComparison.Ordinal); // transparency was flattened, not kept
    }

    [Fact]
    public void The_saved_pdf_passes_qpdf_check()
    {
        var path = Build(builder =>
        {
            builder.AddJpegPage(JpegSegments.Strip(TestJpeg.Make(320, 240, image => TestJpeg.SetOrientation(image, 6))));
            builder.AddJpegPage(JpegSegments.Strip(TestJpeg.Make(configure: image => image.ColorSpace = ColorSpace.CMYK)));
            builder.AddBitmapPage(TransparentBitmap(200, 100), 200, 100, 72);
        });

        var (exitCode, output) = Qpdf("--check", path);

        Assert.True(exitCode == 0, output);
        Assert.Contains("No syntax or stream encoding errors found", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phone_photo_leaves_no_location_or_camera_in_the_pdf_and_stays_upright()
    {
        // As the operation will do with HEIC: decode, turn upright, write a JPEG. The EXIF is kept here on purpose, so
        // that the JPEG reaching the PDF builder carries the phone's GPS and camera.
        byte[] photo;
        using (var frames = ImageFile.Read(TestPdf.Fixture("portrait-gps.heic")))
        {
            var image = frames[0];
            image.AutoOrient();
            image.Quality = 92;
            photo = image.ToByteArray(MagickFormat.Jpeg);
        }

        Assert.Contains("SecretPhoneMake", TestJpeg.Latin1(photo), StringComparison.Ordinal); // control
        using (var withExif = TestJpeg.Decode(photo))
            Assert.NotNull(withExif.GetExifProfile()?.GetValue(ExifTag.GPSLatitude));

        var path = Build(builder => builder.AddJpegPage(JpegSegments.Strip(photo)));

        var text = TestJpeg.Latin1(File.ReadAllBytes(path));
        Assert.DoesNotContain("SecretPhoneMake", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretPhoneModel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exif", text, StringComparison.Ordinal);
        using var pdf = PdfDocument.Open(path);
        var page = pdf.Render(0, 150);
        Assert.Equal((240, 320), (page.Width, page.Height));
        Assert.Equal(Colour.White, At(page, 50 / 240.0, 40 / 320.0)); // the white block is at the top-left
        Assert.NotEqual(Colour.White, At(page, 190 / 240.0, 280 / 320.0));
    }

    [Theory]
    [InlineData("bogus DQT index")]
    [InlineData("not a JPEG")]
    [InlineData("cut before SOF")]
    [InlineData("cut before SOS")]
    public void A_jpeg_pdfium_cannot_read_fails_cleanly_and_leaves_the_document_usable(string damage)
    {
        // pdfium's FPDFImageObj_LoadJpegFileInline says TRUE for all of these; left unchecked, each became a blank page.
        var good = TestJpeg.Make();
        var damaged = damage switch
        {
            // None of these gets through JpegSegments, but the builder must not count on it: libjpeg refuses each header.
            "bogus DQT index" => new JpegImage(WithByte(good, good.AsSpan().IndexOf([(byte)0xFF, (byte)0xDB]) + 4, 0x0F), 96, 64, 1),
            "not a JPEG" => new JpegImage("not a jpeg at all"u8.ToArray(), 96, 64, 1),
            "cut before SOF" => new JpegImage(good[..good.AsSpan().IndexOf([(byte)0xFF, (byte)0xC0])], 96, 64, 1),
            _ => new JpegImage(good[..good.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA])], 96, 64, 1),
        };

        using var builder = new PdfBuilder();
        Assert.Throws<InvalidDataException>(() => builder.AddJpegPage(damaged));
        Assert.Equal(0, builder.PageCount);

        builder.AddJpegPage(JpegSegments.Strip(good));
        var path = _dir.Combine("after-failure.pdf");
        using (var file = File.Create(path))
            builder.Save(file);

        using var pdf = PdfDocument.Open(path);
        Assert.Equal(1, pdf.PageCount);
        Assert.Equal(Colour.Red, At(pdf.Render(0, 150), 0.25, 0.25));
    }

    [Fact]
    public void A_jpeg_without_quantization_tables_gets_past_pdfium_so_the_tables_are_checked_before()
    {
        // pdfium only reads the header (libjpeg's jpeg_read_header), which does not need the tables until decoding: the
        // page is embedded and renders black. JpegSegments refuses such a JPEG (its tests), so the builder is given it
        // directly here, to show that the check has to happen before the builder.
        var jpeg = new JpegImage(TestJpeg.WithoutSegments(TestJpeg.Make(), 0xDB), 96, 64, 1);

        var path = Build(builder => builder.AddJpegPage(jpeg));

        using var pdf = PdfDocument.Open(path);
        var page = pdf.Render(0, 150);
        Assert.Equal((96, 64), (page.Width, page.Height));
        Assert.Equal((0, 0, 0, 255), TestPdf.Pixel(page, 24, 16)); // where the red block is
        Assert.Equal((0, 0, 0, 255), TestPdf.Pixel(page, 72, 48)); // where the blue block is
    }

    [Fact]
    public void The_pdf_says_only_that_flevert_produced_it()
    {
        // docs/specs/pdf.md: the only metadata of a PDF Flevert makes is Producer: Flevert. No creation date, no
        // "PDFium" as Creator (FPDF_CreateNewDocument writes both), no XMP. Saved twice, as Save allows.
        var first = _dir.Combine("first.pdf");
        var second = _dir.Combine("second.pdf");
        using (var builder = new PdfBuilder())
        {
            builder.AddJpegPage(JpegSegments.Strip(TestJpeg.Make()));
            foreach (var path in new[] { first, second })
            {
                using var file = File.Create(path);
                builder.Save(file);
            }
        }

        foreach (var path in new[] { first, second })
        {
            var structure = ReadStructure(path);
            Assert.Equal(new Dictionary<string, string> { ["/Producer"] = "u:Flevert" }, structure.Info);
            Assert.Equal(["/Pages", "/Type"], structure.CatalogKeys.Order(StringComparer.Ordinal));
            var text = TestJpeg.Latin1(File.ReadAllBytes(path));
            Assert.DoesNotContain("PDFium", text, StringComparison.Ordinal);
            Assert.DoesNotContain("CreationDate", text, StringComparison.Ordinal);
        }

        // The file identifier is random for each file, not copied from some template.
        Assert.NotEqual(ReadStructure(first).Id, ReadStructure(second).Id);
    }

    [Fact]
    public void Eight_threads_building_documents_at_once_do_not_crash()
    {
        var jpeg = JpegSegments.Strip(TestJpeg.Make(320, 240, image => TestJpeg.SetOrientation(image, 6)));
        var bitmap = TransparentBitmap(200, 100);
        var errors = new List<Exception>();

        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            try
            {
                for (var round = 0; round < 5; round++)
                {
                    using var builder = new PdfBuilder();
                    builder.AddJpegPage(jpeg);
                    builder.AddBitmapPage(bitmap, 200, 100, 72);
                    builder.AddJpegPage(jpeg, 300);
                    var path = _dir.Combine($"thread {t} round {round}.pdf");
                    using (var file = File.Create(path))
                        builder.Save(file);
                    using var pdf = PdfDocument.Open(path);
                    Assert.Equal(3, pdf.PageCount);
                    Assert.Equal(Colour.Red, At(pdf.Render(0, 72), 0.75, 0.25));
                }
            }
            catch (Exception e)
            {
                lock (errors)
                    errors.Add(e);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Empty(errors);
    }

    public static TheoryData<int, int, int, double?, double, double> PageSizes() => new()
    {
        // pixels, orientation, DPI → page in points
        { 320, 240, 1, 150, 153.6, 115.2 },
        { 320, 240, 6, 150, 115.2, 153.6 },     // a quarter turn swaps the sides
        { 320, 240, 8, null, 115.2, 153.6 },    // unknown DPI: 150
        { 320, 240, 3, 72, 320, 240 },          // a half turn does not
        { 595, 841, 1, 72, 595, 841 },          // A4 at 72 DPI, rounded down, fits as it is
        { 595, 842, 1, 72, 595 * A4Long / 842, A4Long }, // rounded up it is a hair over A4's 841.89 pt: scaled
        { 4000, 3000, 1, null, A4Long, A4Long * 3 / 4 },
        { 3000, 4000, 1, 300, A4Long * 3 / 4, A4Long },
        { 4000, 3000, 5, 72, A4Long * 3 / 4, A4Long },
        { 10_000, 100, 1, 150, A4Long, A4Long / 100 },
        { 320, 240, 1, double.NaN, 153.6, 115.2 },              // a DPI that makes no sense counts as unknown
        { 320, 240, 1, 0, 153.6, 115.2 },
        { 320, 240, 1, -300, 153.6, 115.2 },
        { 320, 240, 1, double.PositiveInfinity, 153.6, 115.2 },
        { 320, 240, 1, 1e9, 153.6, 115.2 },
    };

    [Theory]
    [MemberData(nameof(PageSizes))]
    public void The_page_is_the_image_at_its_dpi_and_at_most_a4_on_its_long_side(
        int width, int height, int orientation, double? dpi, double pageWidth, double pageHeight)
    {
        AssertSize(pageWidth, pageHeight, PdfBuilder.PageSize(width, height, orientation, dpi));
    }

    [Fact]
    public void Refuses_more_pages_than_the_limit()
    {
        using (var standard = new PdfBuilder())
            Assert.Equal(PdfDocument.MaxPages, standard.MaxPages); // what Flevert itself opens
        using var builder = new PdfBuilder(maxPages: 2, maxBitmapPixels: PdfBuilder.MaxBitmapPixels);
        var bitmap = TransparentBitmap(4, 4);
        builder.AddBitmapPage(bitmap, 4, 4);
        builder.AddBitmapPage(bitmap, 4, 4);

        var error = Assert.Throws<PdfException>(() => builder.AddBitmapPage(bitmap, 4, 4));

        Assert.Equal(PdfError.TooManyPages, error.Error);
        Assert.Equal(2, builder.PageCount);
    }

    [Fact]
    public void Refuses_a_bitmap_past_the_pixel_limit_and_a_buffer_of_the_wrong_size()
    {
        Assert.Equal(100_000_000, PdfBuilder.MaxBitmapPixels);
        using var builder = new PdfBuilder(maxPages: 10, maxBitmapPixels: 100);

        Assert.Equal(PdfError.TooLarge, Assert.Throws<PdfException>(() => builder.AddBitmapPage(new byte[20 * 10 * 4], 20, 10)).Error);
        Assert.Throws<ArgumentException>(() => builder.AddBitmapPage(new byte[(5 * 5 * 4) - 1], 5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddBitmapPage([], 0, 5));
        Assert.Equal(0, builder.PageCount);
    }

    [Fact]
    public void Saving_nothing_is_refused()
    {
        using var builder = new PdfBuilder();

        Assert.Throws<InvalidOperationException>(() => builder.Save(new MemoryStream()));
    }

    [Fact]
    public void A_stream_that_fails_while_saving_reports_its_own_error()
    {
        using var builder = new PdfBuilder();
        builder.AddJpegPage(JpegSegments.Strip(TestJpeg.Make()));

        var error = Assert.Throws<IOException>(() => builder.Save(new FailingStream()));

        Assert.Equal("The disk is full.", error.Message);
        using var good = new MemoryStream();
        builder.Save(good);
        Assert.StartsWith("%PDF-", TestJpeg.Latin1(good.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_disposed_builder_refuses_further_use()
    {
        var builder = new PdfBuilder();
        builder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => builder.AddBitmapPage(TransparentBitmap(4, 4), 4, 4));
        Assert.Throws<ObjectDisposedException>(() => builder.Save(new MemoryStream()));
    }

    private string Build(Action<PdfBuilder> pages)
    {
        var path = _dir.Combine($"built {Guid.NewGuid():N}.pdf");
        using var builder = new PdfBuilder();
        pages(builder);
        using var file = File.Create(path);
        builder.Save(file);
        return path;
    }

    /// <summary>BGRA: the left half opaque green, then red at alpha 128, then the last quarter fully transparent black.</summary>
    private static byte[] TransparentBitmap(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 4;
                (bgra[at], bgra[at + 1], bgra[at + 2], bgra[at + 3]) = x < width / 2 ? ((byte)0, (byte)255, (byte)0, (byte)255)
                    : x < width * 3 / 4 ? ((byte)0, (byte)0, (byte)255, (byte)128)
                    : ((byte)0, (byte)0, (byte)0, (byte)0);
            }
        }

        return bgra;
    }

    private static Colour At(PdfBitmap page, double fx, double fy)
    {
        var (r, g, b, _) = TestPdf.Pixel(page, (int)(page.Width * fx), (int)(page.Height * fy));
        return Classify(r, g, b);
    }

    private static Colour Classify(int r, int g, int b) => (r, g, b) switch
    {
        ( > 200, < 60, < 60) => Colour.Red,
        ( < 60, < 60, > 200) => Colour.Blue,
        ( < 60, > 200, < 60) => Colour.Green,
        ( > 230, > 230, > 230) => Colour.White,
        _ => Colour.Other,
    };

    private static void AssertSize(double width, double height, PdfPageSize actual)
    {
        Assert.True(Math.Abs(actual.Width - width) < 0.01 && Math.Abs(actual.Height - height) < 0.01,
            $"expected {width} x {height} pt, got {actual.Width} x {actual.Height}");
    }

    private static byte[] WithByte(byte[] bytes, int index, byte value)
    {
        var copy = (byte[])bytes.Clone();
        copy[index] = value;
        return copy;
    }

    /// <summary>The document's Info, its catalog's keys and the first part of its /ID, as qpdf reads them.</summary>
    private static (Dictionary<string, string> Info, List<string> CatalogKeys, string Id) ReadStructure(string path)
    {
        var (exitCode, output) = Qpdf("--json=2", "--json-key=qpdf", path);
        Assert.True(exitCode == 0, output);
        using var json = JsonDocument.Parse(output);
        var objects = json.RootElement.GetProperty("qpdf")[1];
        JsonElement Resolve(JsonElement reference) => objects.GetProperty("obj:" + reference.GetString()).GetProperty("value");

        var trailer = objects.GetProperty("trailer").GetProperty("value");
        var info = trailer.TryGetProperty("/Info", out var reference)
            ? Resolve(reference).EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.ToString())
            : [];
        var catalog = Resolve(trailer.GetProperty("/Root")).EnumerateObject().Select(entry => entry.Name).ToList();
        return (info, catalog, trailer.GetProperty("/ID")[0].GetString()!);
    }

    private static (int ExitCode, string Output) Qpdf(params string[] arguments)
    {
        var start = new ProcessStartInfo(QpdfPath()) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error);
    }

    // deps\qpdf\bin\qpdf.exe in the repository the tests were built from (scripts/fetch-deps.ps1).
    private static string QpdfPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "deps", "qpdf", "bin", "qpdf.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("deps\\qpdf\\bin\\qpdf.exe is missing. Run scripts\\fetch-deps.ps1.");
    }

    private sealed class FailingStream : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("The disk is full.");

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("The disk is full.");
    }
}
