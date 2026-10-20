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

        Assert.Contains(("pdf.compress", "Compress/Lossless"), ops);
        Assert.Contains(("pdf.compress-sharing", "Compress/For sharing"), ops);
        Assert.Equal(ops.FindIndex(op => op.Id == "pdf.compress") + 1, ops.FindIndex(op => op.Id == "pdf.compress-sharing"));
        Assert.Equal(1, PdfOperations.All.OfType<PdfCompressSharing>().Single().MaxParallel);
    }

    [Fact]
    public async Task A_twenty_page_scan_at_300_dpi_comes_out_at_most_40_percent_as_large_and_looks_the_same()
    {
        // The spike's fixture (docs/specs/pdf.md, P1): 20 A4 pages scanned as JPEGs at 300 DPI.
        var scans = Enumerable.Range(0, 4).Select(seed => Jpeg(ScanJpeg(seed), 2480, 3508)).ToList();
        var input = _dir.Combine("Hồ sơ scan.pdf");
        Write(input, scans, [.. Enumerable.Range(0, 20).Select(p => new[] { (p % 4, FullPage) })]);
        var timer = Stopwatch.StartNew();

        var result = await Op().RunAsync(input, CancellationToken.None);

        var seconds = timer.Elapsed.TotalSeconds;
        var (before, after) = (new FileInfo(input).Length, new FileInfo(result).Length);
        output.WriteLine($"{before} → {after} bytes ({100.0 * after / before:0}%) in {seconds:0.0} s");
        Assert.Equal(_dir.Combine("Hồ sơ scan (small).pdf"), result);
        Assert.True(after <= before * 0.4, $"{before} → {after} bytes");
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
        using var mask = new MagickImage("gradient:white-black", 2000, 2000);
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

        Assert.True(new FileInfo(result).Length < new FileInfo(input).Length / 4);
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
