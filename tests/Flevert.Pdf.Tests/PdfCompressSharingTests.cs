using System.Diagnostics;
using ImageMagick;
using Flevert.Core;
using Xunit.Abstractions;
using static Flevert.Pdf.Tests.SharingPdf;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/pdf.md, P1: <c>Compress > For sharing</c>, with the real qpdf and the worker this solution built.</summary>
[Collection(WorkerProcesses.Name)]
public sealed class PdfCompressSharingTests(ITestOutputHelper output) : IDisposable
{
    private const string AlreadySmall = "This PDF is already small: compressing it for sharing would not make it much smaller.";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private PdfCompressSharing Op() => new(Qpdf.NextToApp, TestPdf.Worker) { ScratchRoot = _dir.Path };

    [Fact]
    public void It_is_compress_for_sharing_beside_lossless_in_the_menu()
    {
        var ops = PdfOperations.All.Select(op => (op.Id, op.MenuPath)).ToList();

        Assert.Contains(("pdf.compress", "Compress/Smaller, same quality"), ops);
        Assert.Contains(("pdf.compress-sharing", "Compress/Smallest, for sharing"), ops);
        Assert.Equal(ops.FindIndex(op => op.Id == "pdf.compress") + 1, ops.FindIndex(op => op.Id == "pdf.compress-sharing"));
        Assert.Equal(1, PdfOperations.All.OfType<PdfCompressSharing>().Single().MaxParallel);
    }

    [Fact]
    public async Task A_twenty_page_scan_at_300_dpi_comes_out_at_most_40_percent_as_large_and_looks_the_same()
    {
        // The spike's fixture (docs/specs/pdf.md, P1): 20 A4 pages scanned as JPEGs at 300 DPI, each a different page,
        // so the time and the size are those of 20 images decoded, shrunk and encoded. With a sensor's noise (SharingPdf):
        // measured 28% (22.2 to 6.2 MB); the clean scan below is the harder case.
        var scans = Enumerable.Range(0, 20).Select(seed => Jpeg(ScanJpeg(seed), 2480, 3508)).ToList();
        var input = _dir.Combine("Hồ sơ scan.pdf");
        Write(input, scans, [.. Enumerable.Range(0, 20).Select(p => new[] { (p, FullPage) })]);
        var timer = Stopwatch.StartNew();

        var result = await Op().RunAsync(input, CancellationToken.None);

        var seconds = timer.Elapsed.TotalSeconds;
        var (before, after) = (new FileInfo(input).Length, new FileInfo(result).Length);
        var encoded = ImageSizes(result).Count(size => size == (1240, 1754));
        output.WriteLine($"{before} → {after} bytes ({100.0 * after / before:0}%) in {seconds:0.0} s, {encoded} images encoded");
        Assert.Equal(_dir.Combine("Hồ sơ scan (small).pdf"), result);
        Assert.True(after <= before * 0.4, $"{before} → {after} bytes");
        Assert.Equal(20, encoded);
        Assert.True(seconds / encoded <= 2, $"{seconds / encoded:0.00} s per image encoded");
        Assert.True(seconds <= 20 * 2, $"{seconds:0.0} s for 20 pages");
        using (var pdf = PdfDocument.Open(result))
            Assert.Equal(20, pdf.PageCount);
        foreach (var page in new[] { 0, 3 })
        {
            var difference = MeanDifference(input, result, page);
            output.WriteLine($"page {page + 1}: {difference:0.00}");
            Assert.True(difference <= 3, $"page {page + 1} differs by {difference:0.00} on average");
        }
    }

    [Fact]
    public async Task A_twenty_page_clean_scan_comes_out_at_most_half_as_large_and_looks_the_same()
    {
        // The same pages without sensor noise: a cleaner scan has less to gain. Measured 47% (2026-10-20, Windows, the
        // ImageMagick of this solution); the bound is that, rounded up, not the 40% of the noisy scan.
        var scans = Enumerable.Range(0, 20).Select(seed => Jpeg(ScanJpeg(seed, noise: false), 2480, 3508)).ToList();
        var input = _dir.Combine("clean scan.pdf");
        Write(input, scans, [.. Enumerable.Range(0, 20).Select(p => new[] { (p, FullPage) })]);

        var result = await Op().RunAsync(input, CancellationToken.None);

        var (before, after) = (new FileInfo(input).Length, new FileInfo(result).Length);
        output.WriteLine($"{before} → {after} bytes ({100.0 * after / before:0}%)");
        Assert.True(after <= before * 0.5, $"{before} → {after} bytes");
        Assert.True(MeanDifference(input, result, 0) <= 3);
    }

    [Fact]
    public async Task An_image_drawn_at_two_sizes_is_shrunk_for_the_larger_and_both_look_the_same()
    {
        // Full page on page 1 (300 DPI), a quarter of it on page 2 (600 DPI): 300 DPI is what decides.
        var input = _dir.Combine("reuse.pdf");
        Write(input, [Jpeg(ScanJpeg(3), 2480, 3508)], [(0, FullPage)], [(0, Matrix(A4Width / 2, A4Height / 2, 100, 100))]);

        var result = await Op().RunAsync(input, CancellationToken.None);

        Assert.True(new FileInfo(result).Length < new FileInfo(input).Length / 2);
        Assert.True(MeanDifference(input, result, 0) <= 3);
        Assert.True(MeanDifference(input, result, 1) <= 3);
        Assert.Equal((1240, 1754), ImageSizes(result).Single());
    }

    [Fact]
    public async Task An_image_with_a_soft_mask_keeps_it_and_looks_the_same()
    {
        // A 2000 px square drawn 6 inches wide (333 DPI) with a gradient mask, over a 300 DPI scan.
        using var picture = ScanPage(6, 2000, 2000);
        using var mask = Gradient(2000, 2000, (255, 255, 255), (0, 0, 0));
        var input = _dir.Combine("mask.pdf");
        Write(input,
        [
            Jpeg(ScanJpeg(1), 2480, 3508),
            new("/Type /XObject /Subtype /Image /Width 2000 /Height 2000 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /SMask {2}", Flate(picture)),
            new("/Type /XObject /Subtype /Image /Width 2000 /Height 2000 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode", Flate(mask, gray: true)),
        ], [(0, FullPage), (1, Matrix(432, 432, 80, 200))]);

        var result = await Op().RunAsync(input, CancellationToken.None);

        Assert.True(MeanDifference(input, result, 0) <= 3);
        Assert.Contains("/SMask", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(result)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_image_stored_as_flate_becomes_a_jpeg_and_looks_the_same()
    {
        using var scan = ScanPage(4);
        var input = _dir.Combine("flate.pdf");
        Write(input, [new("/Type /XObject /Subtype /Image /Width 2480 /Height 3508 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", Flate(scan))], [(0, FullPage)]);

        var result = await Op().RunAsync(input, CancellationToken.None);

        // Lossless pixels of a noisy scan: the JPEG at half the resolution is a small part of them (measured 9.4 MB to 0.31 MB).
        var (before, after) = (new FileInfo(input).Length, new FileInfo(result).Length);
        output.WriteLine($"{before} → {after} bytes");
        Assert.True(after < before / 10, $"{before} → {after} bytes");
        Assert.True(MeanDifference(input, result, 0) <= 3);
    }

    [Fact]
    public async Task A_cmyk_jpeg_with_an_inverted_decode_looks_the_same()
    {
        // As Photoshop writes CMYK JPEGs into PDFs: inverted, and /Decode turning it back.
        using var scan = ScanPage(5);
        scan.ColorSpace = ColorSpace.CMYK;
        scan.Negate(Channels.All);
        scan.Quality = 85;
        var input = _dir.Combine("cmyk.pdf");
        Write(input, [Jpeg(scan.ToByteArray(MagickFormat.Jpeg), 2480, 3508, "/DeviceCMYK", "/Decode [1 0 1 0 1 0 1 0]")], [(0, FullPage)]);

        var result = await Op().RunAsync(input, CancellationToken.None);

        Assert.True(MeanDifference(input, result, 0) <= 3, $"{MeanDifference(input, result, 0):0.00}");
    }

    [Fact]
    public async Task Images_at_150_dpi_or_less_are_left_alone_and_the_pdf_is_skipped_as_already_small()
    {
        // 1240 px across A4: 150 DPI.
        using var page = ScanPage(2, 1240, 1754);
        page.Quality = 85;
        var input = _dir.Combine("small.pdf");
        Write(input, [Jpeg(page.ToByteArray(MagickFormat.Jpeg), 1240, 1754)], [(0, FullPage)]);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(AlreadySmall, ex.Message);
        Assert.Equal(["small.pdf"], TestPdf.FileNames(_dir.Path));
    }

    [Fact]
    public async Task A_one_bit_image_is_left_alone()
    {
        // A fax-like stencil at 300 DPI: JPEG would blur it and make it larger.
        var bits = new byte[(2480 / 8) * 3508];
        new Random(7).NextBytes(bits);
        var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(bits);
        var input = _dir.Combine("fax.pdf");
        Write(input, [new("/Type /XObject /Subtype /Image /Width 2480 /Height 3508 /ImageMask true /BitsPerComponent 1 /Filter /FlateDecode", packed.ToArray())], [(0, FullPage)]);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(AlreadySmall, ex.Message);
    }

    [Fact]
    public async Task Through_the_runner_a_pdf_without_images_is_skipped_and_the_scratch_folder_is_gone()
    {
        var input = _dir.Combine("text.pdf");
        TestPdf.WriteBlank(input, 2);
        using var logs = new TempDir();

        var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(Op(), [input], null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Skipped);
        Assert.Equal(AlreadySmall, result.Error);
        Assert.Equal(["text.pdf"], Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Reports_the_pages_as_it_looks_at_their_images()
    {
        var input = _dir.Combine("three.pdf");
        Write(input, [Jpeg(ScanJpeg(0), 2480, 3508)], [(0, FullPage)], [(0, FullPage)], [(0, FullPage)]);
        var reports = new List<FileProgress>();

        await Op().RunAsync(input, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal([1, 2, 3], reports.Select(r => r.Position));
    }

    [Fact]
    public async Task An_rgb_image_whose_every_pixel_is_gray_becomes_a_gray_jpeg_and_looks_the_same()
    {
        // A black-and-white scan stored as RGB: ImageMagick writes a one-component JPEG, so the dictionary must say DeviceGray.
        using var scan = ScanPage(8);
        scan.Grayscale();
        scan.ColorType = ColorType.TrueColor;
        var input = _dir.Combine("bw.pdf");
        Write(input, [new("/Type /XObject /Subtype /Image /Width 2480 /Height 3508 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", Flate(scan))], [(0, FullPage)]);

        var result = await Op().RunAsync(input, CancellationToken.None);

        Assert.True(MeanDifference(input, result, 0) <= 3);
        Assert.Matches(@"/ColorSpace\s*/DeviceGray", System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(result)));
    }

    [Fact]
    public async Task An_image_drawn_inside_a_form_xobject_is_left_as_it_is()
    {
        // pdfium gives the size of an image inside a form in the form's space, not the page's: never made smaller.
        var input = _dir.Combine("form.pdf");
        var jpeg = ScanJpeg(0);
        WriteObjects(input,
        [
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {A4}] /Resources << /XObject << /Fm0 4 0 R >> >> /Contents 6 0 R >>"),
            Stream($"/Type /XObject /Subtype /Form /BBox [0 0 {A4}] /Resources << /XObject << /Im0 5 0 R >> >>", $"q {FullPage} cm /Im0 Do Q"),
            Stream(Jpeg(jpeg, 2480, 3508).Dictionary, jpeg),
            Stream("", "/Fm0 Do"),
        ]);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(AlreadySmall, ex.Message);
    }

    [Fact]
    public async Task An_image_a_stamp_annotation_also_shows_is_kept_at_full_resolution()
    {
        // The page draws it at 300 DPI, and so does the stamp's appearance, which the worker does not see: it is also
        // drawn outside the page's content, so it is not replaced.
        var input = _dir.Combine("stamp.pdf");
        WriteStamped(input, stampImage: 4);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(AlreadySmall, ex.Message);
    }

    [Fact]
    public async Task A_copy_of_the_page_image_in_a_stamp_annotation_keeps_full_resolution()
    {
        // Two objects with the same data and dictionary: the page's is replaced, the stamp's stays as it is.
        var input = _dir.Combine("stamp copy.pdf");
        WriteStamped(input, stampImage: 8);

        var result = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal([(1240, 1754), (2480, 3508)], ImageSizes(result).Order());
    }

    [Fact]
    public async Task Images_with_the_same_data_but_different_dictionaries_are_none_of_them_replaced()
    {
        // The second is inverted by /Decode: the one JPEG the worker made is right for only one of them.
        var jpeg = ScanJpeg(0);
        var input = _dir.Combine("decode.pdf");
        Write(input, [Jpeg(jpeg, 2480, 3508), Jpeg(jpeg, 2480, 3508, extra: "/Decode [1 0 1 0 1 0]")], [(0, FullPage)], [(1, FullPage)]);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(AlreadySmall, ex.Message);
    }

    [Fact]
    public async Task An_image_with_a_colour_key_mask_is_left_as_it_is()
    {
        // A JPEG's lossy pixels would no longer match the key colours.
        var input = _dir.Combine("mask.pdf");
        Write(input, [Jpeg(ScanJpeg(0), 2480, 3508, extra: "/Mask [240 255 240 255 240 255]")], [(0, FullPage)]);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(AlreadySmall, ex.Message);
    }

    private static readonly string A4 = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{A4Width} {A4Height}");

    // A page that draws image 4 (a 300 DPI scan) over the whole page, with a stamp annotation whose appearance draws
    // image stampImage: 4 itself, or 8, a copy of it (same data, same dictionary).
    private static void WriteStamped(string path, int stampImage)
    {
        var jpeg = ScanJpeg(0);
        var image = Stream(Jpeg(jpeg, 2480, 3508).Dictionary, jpeg);
        WriteObjects(path,
        [
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Count 1 /Kids [3 0 R] >>"),
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {A4}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R] >>"),
            image,
            Stream("", $"q {FullPage} cm /Im0 Do Q"),
            Ascii("<< /Type /Annot /Subtype /Stamp /Rect [0 0 595 842] /AP << /N 7 0 R >> >>"),
            Stream($"/Type /XObject /Subtype /Form /BBox [0 0 595 842] /Resources << /XObject << /Im0 {stampImage} 0 R >> >>", "q 595 0 0 842 0 0 cm /Im0 Do Q"),
            image,
        ]);
    }

    [Fact]
    public async Task A_scratch_drive_without_twice_the_pdfs_size_free_is_refused_before_anything_is_written()
    {
        var input = _dir.Combine("scan.pdf");
        Write(input, [Jpeg(ScanJpeg(0), 2480, 3508)], [(0, FullPage)]);
        var op = new PdfCompressSharing(Qpdf.NextToApp, TestPdf.Worker) { ScratchRoot = _dir.Path, FreeSpace = _ => (2 * new FileInfo(input).Length) - 1 };

        var ex = await Assert.ThrowsAsync<IOException>(() => op.RunAsync(input, CancellationToken.None));

        Assert.Equal($"There is not enough free space on {Path.GetPathRoot(_dir.Path)} to compress this PDF for sharing.", ex.Message);
        Assert.Equal(["scan.pdf"], TestPdf.FileNames(_dir.Path));
        Assert.Empty(Directory.GetDirectories(_dir.Path));
    }

    [Fact]
    public async Task A_working_file_qpdf_cannot_write_fails_as_a_disk_problem_not_as_a_damaged_pdf()
    {
        // Folders where qpdf writes the stream data files: it cannot create them, as on a full disk.
        var input = _dir.Combine("scan.pdf");
        Write(input, [Jpeg(ScanJpeg(0), 2480, 3508)], [(0, FullPage)]);
        var op = new PdfCompressSharing(Qpdf.NextToApp, TestPdf.Worker)
        {
            ScratchRoot = _dir.Path,
            BeforeDescribing = scratch =>
            {
                for (var number = 1; number <= 10; number++)
                    Directory.CreateDirectory(Path.Combine(scratch, $"s-{number}"));
            },
        };

        var ex = await Assert.ThrowsAsync<IOException>(() => op.RunAsync(input, CancellationToken.None));

        Assert.Equal($"Flevert could not write its working files on {Path.GetPathRoot(_dir.Path)} to compress this PDF for sharing: check that the disk has free space.", ex.Message);
        Assert.Equal(["scan.pdf"], TestPdf.FileNames(_dir.Path));
        Assert.Empty(Directory.GetDirectories(_dir.Path));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00 }, 1)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xC2, 0x00, 0x11, 0x08, 0x00, 0x01, 0x00, 0x01, 0x03, 0x01, 0x22, 0x00 }, 3)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, 0)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x01 }, 0)]
    public void The_components_of_a_jpeg_are_read_from_its_frame_header(byte[] jpeg, int components)
    {
        Assert.Equal(components, PdfRenderWorker.JpegComponents(jpeg));
    }

    // The pixel size of every image in the PDF, from its dictionary, which is never in an object stream: a stream's.
    private static List<(int Width, int Height)> ImageSizes(string path)
    {
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));
        return [.. System.Text.RegularExpressions.Regex.Matches(text, @"<<[^<>]*/Subtype\s*/Image[^<>]*>>")
            .Select(m => (Number(m.Value, "Width"), Number(m.Value, "Height")))];

        static int Number(string dictionary, string key) => int.Parse(
            System.Text.RegularExpressions.Regex.Match(dictionary, $@"/{key}\s+(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class SyncProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }
}
