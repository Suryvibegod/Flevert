using System.Text.Json;
using ImageMagick;
using Flevert.Core;
using Flevert.Image;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/pdf.md, <c>pdf.from-images</c> (Combine into PDF): every selected image, one page each, in one PDF.</summary>
public sealed class ImagesToPdfTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private readonly TempDir _dir = new();

    static ImagesToPdfTests() => MagickSetup.EnsureInitialized();

    public void Dispose() => _dir.Dispose();

    private enum Colour
    {
        Red,
        Blue,
        Green,
        White,
        Black,
        Other,
    }

    [Fact]
    public void The_operation_is_the_one_in_the_spec()
    {
        var op = new ImagesToPdf();

        Assert.Equal(("pdf.from-images", "Combine into PDF"), (op.Id, op.MenuPath));
        Assert.Equal(ImageFile.ReadableExtensions.Order(), op.Extensions.Order());
        Assert.IsAssignableFrom<IBatchOperation>(op);
    }

    [Fact]
    public async Task Twelve_mixed_images_become_one_pdf_of_twelve_pages_in_order_upright_at_their_size_and_in_their_colours()
    {
        var pages = Twelve();
        var progress = new Recorder();

        var output = await Run(pages.Select(p => p.Path).ToList(), progress);

        Assert.Equal(_dir.Combine("page 1 (+11).pdf"), output);
        Assert.Equal(pages.Select(p => Path.GetFileName(p.Path)).Append("page 1 (+11).pdf").Order(StringComparer.Ordinal), FileNames());
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"image {i} of 12"), progress.Reports.Select(r => $"{r.Unit} {r.Position} of {r.Total}"));

        using var pdf = PdfDocument.Open(output);
        Assert.Equal(12, pdf.PageCount);
        for (var index = 0; index < pages.Count; index++)
        {
            var (path, width, height, check) = pages[index];
            var size = pdf.GetPageSize(index);
            Assert.True(Math.Abs(size.Width - width) < 0.01 && Math.Abs(size.Height - height) < 0.01,
                $"page {index + 1} ({Path.GetFileName(path)}): expected {width} x {height} pt, got {size.Width} x {size.Height}");
            check(pdf.Render(index, 150));
        }
    }

    [Fact]
    public async Task Jpegs_are_embedded_as_their_stripped_bytes_and_no_camera_location_or_exif_is_left_anywhere()
    {
        var pages = Twelve();
        var jpegs = pages.Select(p => p.Path).Where(p => Path.GetExtension(p) is ".jpg" or ".jpeg").ToList();
        Assert.Equal(3, jpegs.Count);
        Assert.Contains("SecretPhoneMake", TestJpeg.Latin1(File.ReadAllBytes(jpegs[0])), StringComparison.Ordinal); // control
        Assert.Contains("SecretPhoneMake", TestJpeg.Latin1(File.ReadAllBytes(TestPdf.Fixture("portrait-gps.heic"))), StringComparison.Ordinal);

        var output = await Run(pages.Select(p => p.Path).ToList());

        var bytes = File.ReadAllBytes(output);
        foreach (var jpeg in jpegs)
            Assert.True(bytes.AsSpan().IndexOf(JpegSegments.Strip(File.ReadAllBytes(jpeg)).Bytes) >= 0, $"{Path.GetFileName(jpeg)} is not embedded as it is");
        var text = TestJpeg.Latin1(bytes);
        foreach (var secret in new[] { "SecretPhoneMake", "SecretPhoneModel", "Exif", "PDFium", "CreationDate" })
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pdf_passes_qpdf_check_and_says_only_that_flevert_produced_it()
    {
        var output = await Run(Twelve().Select(p => p.Path).ToList());

        var check = await Qpdf("--check", output);
        Assert.True(check.ExitCode == 0, check.StandardOutput + check.StandardError);
        Assert.Contains("No syntax or stream encoding errors found", check.StandardOutput, StringComparison.Ordinal);

        var json = await Qpdf("--json=2", "--json-key=qpdf", output);
        Assert.True(json.ExitCode == 0, json.StandardError);
        using var document = JsonDocument.Parse(json.StandardOutput);
        var objects = document.RootElement.GetProperty("qpdf")[1];
        var info = objects.GetProperty("obj:" + objects.GetProperty("trailer").GetProperty("value").GetProperty("/Info").GetString()).GetProperty("value");
        Assert.Equal(["/Producer"], info.EnumerateObject().Select(entry => entry.Name));
        Assert.Equal("u:Flevert", info.GetProperty("/Producer").GetString());
    }

    [Fact]
    public async Task One_image_gives_a_pdf_of_its_own_name_and_never_replaces_one_that_is_there()
    {
        var photo = TestImages.Picture(_dir.Combine("Hóa đơn.jpg"), 96, 64, MagickFormat.Jpeg);
        File.WriteAllText(_dir.Combine("Hóa đơn.pdf"), "someone else's");

        var output = await Run([photo]);

        Assert.Equal(_dir.Combine("Hóa đơn (1).pdf"), output);
        Assert.Equal("someone else's", File.ReadAllText(_dir.Combine("Hóa đơn.pdf")));
        using var pdf = PdfDocument.Open(output);
        Assert.Equal(1, pdf.PageCount);
    }

    [Fact]
    public async Task Every_page_of_a_multi_page_tiff_becomes_a_page_and_its_thumbnails_do_not()
    {
        // page (300 x 200), thumbnail, page (200 x 150), thumbnail; then a Group 4 black-and-white page and a colour page.
        var withThumbnails = Copy("pages-with-thumbnails.tif", "scan 1.tif");
        var mixed = Copy("mixed-g4-colour.tif", "scan 2.tif");

        var output = await Run([withThumbnails, mixed]);

        using var pdf = PdfDocument.Open(output);
        Assert.Equal(4, pdf.PageCount);
        Assert.Equal([(300, 200), (200, 150), (200, 150), (200, 150)],
            Enumerable.Range(0, 4).Select(i => pdf.Render(i, 150)).Select(page => (page.Width, page.Height)));
        Assert.Equal(Colour.White, At(pdf.Render(2, 150), 0.95, 0.95)); // the black-and-white page: white paper
        Assert.Equal(Colour.Red, At(pdf.Render(3, 150), 0.5, 0.5)); // the colour page: a red block in the middle
    }

    [Fact]
    public async Task A_jpegs_colour_profile_with_the_text_it_can_carry_is_left_out_and_the_page_looks_the_same()
    {
        // Final audit of v0.2.0: an ICC profile can name the device and its owner (desc, cprt, dmdd), and pdfium marks
        // every embedded JPEG DeviceRGB anyway, so no viewer uses the profile. The marker stands in for that text.
        var profile = ColorProfiles.SRGB.ToByteArray().Concat("SecretProfileOwner"u8.ToArray()).ToArray();
        var profiled = WriteJpeg("profiled.jpg", TestJpeg.Make(configure: image => image.SetProfile(new ColorProfile(profile))));
        var plain = WriteJpeg("plain.jpg", TestJpeg.Make());
        Assert.Contains("SecretProfileOwner", TestJpeg.Latin1(File.ReadAllBytes(profiled)), StringComparison.Ordinal); // control
        Assert.Contains("ICC_PROFILE", TestJpeg.Latin1(File.ReadAllBytes(profiled)), StringComparison.Ordinal);

        var output = await Run([profiled]);
        var control = await Run([plain]);

        var text = TestJpeg.Latin1(File.ReadAllBytes(output));
        Assert.DoesNotContain("SecretProfileOwner", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ICC_PROFILE", text, StringComparison.Ordinal);
        Assert.Contains("/DeviceRGB", text, StringComparison.Ordinal); // what viewers used before, profile or not
        using var pdf = PdfDocument.Open(output);
        using var controlPdf = PdfDocument.Open(control);
        Assert.True(pdf.Render(0, 150).Bgra.Span.SequenceEqual(controlPdf.Render(0, 150).Bgra.Span), "the page draws differently");
    }

    [Fact]
    public async Task A_jpeg_pdf_viewers_cannot_show_is_re_encoded_upright_and_without_its_metadata()
    {
        // Stored 96 x 64 with EXIF orientation 6, camera and GPS: upright 64 x 96, the red block at the top-right.
        var arithmetic = TestImages.Picture(_dir.Combine("arithmetic.jpg"), 96, 64, MagickFormat.Jpeg, configure: image =>
        {
            image.Settings.SetDefine(MagickFormat.Jpeg, "arithmetic-coding", "true");
            TestJpeg.SetOrientation(image, 6, TestJpeg.PhoneExif(6));
        });
        Assert.Throws<NotSupportedException>(() => JpegSegments.Strip(File.ReadAllBytes(arithmetic))); // control
        Assert.Contains("SecretPhoneMake", TestJpeg.Latin1(File.ReadAllBytes(arithmetic)), StringComparison.Ordinal);

        var output = await Run([arithmetic]);

        using var pdf = PdfDocument.Open(output);
        var page = pdf.Render(0, 150);
        Assert.Equal((64, 96), (page.Width, page.Height));
        Assert.Equal(Colour.Red, At(page, 0.75, 0.25));
        Assert.Equal(Colour.Blue, At(page, 0.25, 0.75));
        var text = TestJpeg.Latin1(File.ReadAllBytes(output));
        Assert.Contains("/DCTDecode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretPhoneMake", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exif", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a JPEG cut short inside its image data", ".jpg")]
    [InlineData("a JPEG whose Huffman tables are gone", ".jpg")]
    [InlineData("a PNG cut short", ".png")]
    [InlineData("not an image at all", ".png")]
    [InlineData("an empty file", ".heic")]
    public async Task A_damaged_image_fails_the_whole_pdf_naming_it_and_nothing_is_written(string damage, string extension)
    {
        var first = TestImages.Picture(_dir.Combine("1.jpg"), 96, 64, MagickFormat.Jpeg);
        var damaged = _dir.Combine("2" + extension);
        var jpeg = TestJpeg.Make(320, 240);
        var png = TestImages.Picture(_dir.Combine("x.png"), 320, 240, MagickFormat.Png);
        var pngBytes = File.ReadAllBytes(png);
        File.Delete(png);
        File.WriteAllBytes(damaged, damage switch
        {
            // ImageMagick decodes this with only a warning; a page missing its lower half would look complete.
            "a JPEG cut short inside its image data" => jpeg[..(jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]) + 200)],
            "a JPEG whose Huffman tables are gone" => TestJpeg.WithoutSegments(jpeg, 0xC4),
            "a PNG cut short" => pngBytes[..(pngBytes.Length / 2)],
            "not an image at all" => "PK\u0003\u0004 this is a zip, or something"u8.ToArray(),
            _ => [],
        });
        var third = TestImages.Picture(_dir.Combine("3.png"), 96, 64, MagickFormat.Png);

        var error = await Assert.ThrowsAsync<BatchInputException>(() => Run([first, damaged, third]));

        Assert.Equal(damaged, error.InputPath);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.Equal(["1.jpg", "2" + extension, "3.png"], FileNames());
    }

    [Fact]
    public async Task Cancelling_while_combining_leaves_nothing_and_returns_every_share()
    {
        var budget = new MemoryBudget(1UL << 30);
        var images = Enumerable.Range(1, 3).Select(i => TestImages.Picture(_dir.Combine($"{i}.png"), 200, 150, MagickFormat.Png)).ToList();
        using var cancel = new CancellationTokenSource();
        var progress = new Recorder(report =>
        {
            if (report.Position == 2)
                cancel.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ImagesToPdf(budget).RunAsync(images, progress, cancel.Token).WaitAsync(Wait));

        Assert.Equal(["1.png", "2.png", "3.png"], FileNames());
        Assert.True(budget.AcquireAsync(budget.Capacity, CancellationToken.None).IsCompletedSuccessfully, "a share was kept after cancelling");
    }

    [Fact]
    public async Task Saving_stops_at_the_first_write_once_cancelled()
    {
        // pdfium cannot be interrupted while it saves up to 1.5 GB; the runner gives a cancelled join only 2 s.
        using var builder = new PdfBuilder();
        builder.AddJpegPage(JpegSegments.Strip(TestJpeg.Make()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var temp = _dir.Combine("temp.partial");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImagesToPdf.Save(builder, temp, cancelled.Token));

        Assert.Equal(0, new FileInfo(temp).Length);
    }

    [Fact]
    public async Task Shares_of_the_memory_budget_are_held_for_the_pages_built_and_all_returned_once_the_pdf_is_written()
    {
        var budget = new MemoryBudget(1UL << 30);
        var images = Enumerable.Range(1, 3).Select(i => TestImages.Picture(_dir.Combine($"{i}.png"), 200, 150, MagickFormat.Png)).ToList();
        var heldWhileBuilding = false;
        var progress = new Recorder(report =>
        {
            if (report.Position != 3)
                return;
            // Pages 1 and 2 are in the builder: a request for the whole budget must not be granted while they are.
            using var probe = new CancellationTokenSource();
            var whole = budget.AcquireAsync(budget.Capacity, probe.Token);
            heldWhileBuilding = !whole.IsCompleted;
            probe.Cancel();
            if (whole.IsCompletedSuccessfully)
                whole.Result.Dispose();
        });

        await new ImagesToPdf(budget).RunAsync(images, progress, CancellationToken.None).WaitAsync(Wait);

        Assert.True(heldWhileBuilding, "the pages in the builder held no share of the budget");
        var after = budget.AcquireAsync(budget.Capacity, CancellationToken.None);
        Assert.True(after.IsCompletedSuccessfully, "a share was kept after the PDF was written");
        (await after).Dispose();
    }

    [Fact]
    public async Task Images_that_together_need_more_memory_than_the_budget_are_refused_instead_of_waiting_for_ever()
    {
        // 400 x 400 px: decoding one takes more than half of 2 MB (so all of it), and each page kept takes 480 000 bytes.
        var budget = new MemoryBudget(2_000_000);
        var images = Enumerable.Range(1, 3).Select(i => TestImages.Picture(_dir.Combine($"{i}.png"), 400, 400, MagickFormat.Png)).ToList();

        var error = await Assert.ThrowsAsync<PdfException>(() => new ImagesToPdf(budget).RunAsync(images, null, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(PdfError.NeedsTooMuchMemory, error.Error);
        Assert.StartsWith("Together the images need more memory than Flevert allows", error.Message, StringComparison.Ordinal);
        Assert.Equal(["1.png", "2.png", "3.png"], FileNames());
        Assert.True(budget.AcquireAsync(budget.Capacity, CancellationToken.None).IsCompletedSuccessfully, "a share was kept after the refusal");
    }

    [Fact]
    public async Task A_batch_with_more_pages_than_a_pdf_holds_is_refused_before_any_image_is_decoded()
    {
        var images = Enumerable.Range(1, 3).Select(i => TestImages.Picture(_dir.Combine($"{i}.png"), 40, 30, MagickFormat.Png)).ToList();
        var progress = new Recorder();

        var error = await Assert.ThrowsAsync<PdfException>(
            () => new ImagesToPdf(MemoryBudget.Shared, maxPages: 2).RunAsync(images, progress, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(PdfError.TooManyPages, error.Error);
        Assert.Equal("A PDF made by Flevert has at most 2 pages.", error.Message);
        Assert.Empty(progress.Reports);
        Assert.Equal(["1.png", "2.png", "3.png"], FileNames());
    }

    [Fact]
    public async Task Images_past_the_content_cap_of_one_pdf_are_refused_with_a_clear_message()
    {
        // 200 x 150 px kept as 90 000 bytes each: two fit in 200 000 bytes, three do not.
        var images = Enumerable.Range(1, 3).Select(i => TestImages.Picture(_dir.Combine($"{i}.png"), 200, 150, MagickFormat.Png)).ToList();

        var error = await Assert.ThrowsAsync<PdfException>(
            () => new ImagesToPdf(MemoryBudget.Shared, maxContentBytes: 200_000).RunAsync(images, null, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(PdfError.TooLarge, error.Error);
        Assert.StartsWith("Together the images are too large for one PDF", error.Message, StringComparison.Ordinal);
        Assert.Equal(["1.png", "2.png", "3.png"], FileNames());
    }

    [Fact]
    public async Task An_image_past_the_pixel_limit_of_a_page_is_refused_naming_it_before_any_image_is_decoded()
    {
        var small = TestImages.Picture(_dir.Combine("1.png"), 40, 30, MagickFormat.Png);
        var large = TestImages.Picture(_dir.Combine("2.jpg"), 400, 300, MagickFormat.Jpeg);
        var progress = new Recorder();

        var error = await Assert.ThrowsAsync<BatchInputException>(
            () => new ImagesToPdf(MemoryBudget.Shared, maxImagePixels: 10_000).RunAsync([small, large], progress, CancellationToken.None).WaitAsync(Wait));

        Assert.Equal(large, error.InputPath);
        Assert.Equal("The image is 400 x 300 pixels; a PDF page made by Flevert holds at most 10000.", error.Message);
        Assert.Empty(progress.Reports);
        Assert.Equal(["1.png", "2.jpg"], FileNames());
    }

    /// <summary>
    /// Twelve images of every kind, named so that natural order is not text order ("page 2" before "page 10"), each a
    /// different size so a page out of place shows: the path, the page size expected (points) and a check of its colours.
    /// </summary>
    private List<(string Path, double Width, double Height, Action<PdfBitmap> Check)> Twelve()
    {
        static double Pt(double pixels, double dpi) => pixels * 72 / dpi;
        static Action<PdfBitmap> Corners(double redX, double redY, double blueX, double blueY) => page =>
        {
            Assert.Equal(Colour.Red, At(page, redX, redY));
            Assert.Equal(Colour.Blue, At(page, blueX, blueY));
        };

        var uprightCorners = Corners(0.25, 0.25, 0.75, 0.75);
        var perInch = (double dpi) => new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        var perCm = (double dpcm) => new Density(dpcm, dpcm, DensityUnit.PixelsPerCentimeter);

        // 1: a phone photo stored 320 x 240 with EXIF orientation 6, camera and GPS, at 200 DPI: upright 240 x 320.
        var phone = _dir.Combine("page 1.jpg");
        File.WriteAllBytes(phone, TestJpeg.Make(320, 240, image =>
        {
            TestJpeg.SetOrientation(image, 6, TestJpeg.PhoneExif(6));
            image.Density = perInch(200);
        }));

        var heic = Copy("portrait-gps.heic", "page 2.heic");

        return
        [
            (phone, Pt(240, 200), Pt(320, 200), Corners(0.75, 0.25, 0.25, 0.75)),
            (heic, Pt(240, 150), Pt(320, 150), page =>
            {
                Assert.Equal(Colour.White, At(page, 50 / 240.0, 40 / 320.0)); // the white block is at the top-left
                Assert.NotEqual(Colour.White, At(page, 190 / 240.0, 280 / 320.0));
            }),
            (TestImages.TransparentPng(_dir.Combine("page 3.png"), 200, 100, perCm(50)), Pt(200, 127), Pt(100, 127), page =>
            {
                Assert.Equal(Colour.Green, At(page, 0.25, 0.5));
                Assert.Equal(Colour.White, At(page, 0.75, 0.5)); // transparent: white, not black
            }),
            (TestImages.Picture(_dir.Combine("page 4.webp"), 112, 84, MagickFormat.WebP), Pt(112, 150), Pt(84, 150), uprightCorners),
            (TestImages.BlueThenRedGif(_dir.Combine("page 5.gif"), 120, 90), Pt(120, 150), Pt(90, 150),
                page => Assert.Equal(Colour.Blue, At(page, 0.5, 0.5))), // the first frame only
            (TestImages.Picture(_dir.Combine("page 6.tiff"), 128, 96, MagickFormat.Tiff, perInch(100)), Pt(128, 100), Pt(96, 100), uprightCorners),
            (TestImages.Picture(_dir.Combine("page 7.bmp"), 136, 102, MagickFormat.Bmp, perCm(40)), Pt(136, 101.6), Pt(102, 101.6), uprightCorners),
            (TestImages.Picture(_dir.Combine("page 8.webp"), 144, 108, MagickFormat.WebP,
                configure: image => image.Settings.SetDefine(MagickFormat.WebP, "lossless", "true")), Pt(144, 150), Pt(108, 150), uprightCorners),
            (TestImages.Picture(_dir.Combine("page 9.avif"), 152, 114, MagickFormat.Avif), Pt(152, 150), Pt(114, 150), uprightCorners),
            (TestImages.GreyPng(_dir.Combine("page 10.png"), 160, 120), Pt(160, 150), Pt(120, 150), page =>
            {
                Assert.Equal(Colour.Black, At(page, 0.25, 0.5));
                Assert.Equal(Colour.White, At(page, 0.75, 0.5));
            }),
            (WriteJpeg("page 11.jpg", TestJpeg.Make(168, 126, image => image.ColorSpace = ColorSpace.CMYK)), Pt(168, 150), Pt(126, 150),
                page => Assert.Equal(Colour.Red, At(page, 0.25, 0.25))), // CMYK red, as printed: (238, 29, 35)
            (WriteJpeg("page 12.jpeg", TestJpeg.Make(176, 132, image => image.Density = perInch(72))), Pt(176, 72), Pt(132, 72), uprightCorners),
        ];
    }

    private string WriteJpeg(string name, byte[] jpeg)
    {
        var path = _dir.Combine(name);
        File.WriteAllBytes(path, jpeg);
        return path;
    }

    private string Copy(string fixture, string name)
    {
        var path = _dir.Combine(name);
        File.Copy(TestPdf.Fixture(fixture), path);
        return path;
    }

    private static Task<string> Run(IReadOnlyList<string> inputs, IProgress<FileProgress>? progress = null) =>
        new ImagesToPdf().RunAsync(inputs, progress, CancellationToken.None).WaitAsync(Wait);

    private string[] FileNames() =>
        Directory.GetFileSystemEntries(_dir.Path).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    private static Task<ExternalToolResult> Qpdf(params string[] arguments) =>
        ExternalTool.RunAsync(Path.Combine(AppContext.BaseDirectory, "qpdf.exe"), arguments, TimeSpan.FromSeconds(30));

    private static Colour At(PdfBitmap page, double fx, double fy)
    {
        var (r, g, b, _) = TestPdf.Pixel(page, (int)(page.Width * fx), (int)(page.Height * fy));
        return (r, g, b) switch
        {
            ( > 200, < 60, < 60) => Colour.Red,
            ( < 60, < 60, > 200) => Colour.Blue,
            ( < 60, > 200, < 60) => Colour.Green,
            ( > 230, > 230, > 230) => Colour.White,
            ( < 25, < 25, < 25) => Colour.Black,
            _ => Colour.Other,
        };
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
