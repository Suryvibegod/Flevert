using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image.Tests;

/// <summary>Animations and multi-page files: what is kept, and what is read at all.</summary>
public sealed class FramesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string id) => ImageOperations.All.Single(op => op.Id == id);

    private string ThreePageTiff()
    {
        var path = _dir.Combine("scan.tiff");
        using var pages = new MagickImageCollection();
        foreach (var color in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
            pages.Add(new MagickImage(color, 32, 32));
        pages.Write(path, MagickFormat.Tiff);
        return path;
    }

    [Fact]
    public async Task An_animation_whose_expanded_frames_exceed_the_area_limit_is_refused_before_expanding()
    {
        // Tiny frames on a huge canvas: a few hundred bytes on disk, 900 MP once every frame is expanded to the canvas.
        // (No negative control: without the guard this would write gigabytes of pixel cache.)
        var input = _dir.Combine("bomb.gif");
        using (var frames = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
            {
                var frame = new MagickImage(color, 1, 1) { AnimationDelay = 10 };
                frame.Page = new MagickGeometry(0, 0, 20_000, 15_000);
                frames.Add(frame);
            }

            frames.Write(input, MagickFormat.Gif);
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Op("image.to-webp").RunAsync(input, CancellationToken.None));

        Assert.Contains("too large", error.Message, StringComparison.Ordinal);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
    }

    // Coalesce and Optimize create new frames that keep calling the progress handler of the frames they replaced; once
    // the garbage collector frees those, the next callback kills the process. Tiny animations never reach a GC.
    [Theory]
    [InlineData("image.compress-balanced")]
    [InlineData("image.to-webp")]
    public async Task A_real_sized_animation_survives_garbage_collection_during_processing(string operationId)
    {
        var input = _dir.Combine("clip.gif");
        using (var frames = new MagickImageCollection())
        {
            for (var i = 0; i < 30; i++)
            {
                var frame = new MagickImage(MagickColors.Gray, 400, 300) { AnimationDelay = 5 };
                frame.AddNoise(NoiseType.Gaussian, 0.5);
                frames.Add(frame);
            }

            // Metadata for Compress to remove, so its result is reliably smaller (random noise alone is not).
            frames[0].Comment = new string('x', 50_000);
            frames.Write(input, MagickFormat.Gif);
        }

        // A crash here kills the test host itself, which is how this test fails.
        using var stop = new CancellationTokenSource();
        var collector = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                GC.Collect();
                await Task.Delay(100);
            }
        });

        try
        {
            for (var round = 0; round < 2; round++)
            {
                try
                {
                    File.Delete(await Op(operationId).RunAsync(input, CancellationToken.None));
                }
                catch (Exception ex) when (ex is ConvertAnything.Core.OperationSkippedException
                                           || ex.Message.Contains("animated GIF", StringComparison.Ordinal))
                {
                    // Compress may find too little to gain on noise; what matters here is that the process survives.
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await collector;
        }
    }

    // ImageMagick reports a TIFF's compression but writes uncompressed unless told: 2 KB became 2 MB, and a crafted few-KB
    // TIFF could fill the disk.
    [Theory]
    [InlineData("image.strip-metadata", "scan.tiff")]
    [InlineData("image.to-tiff", "scan.png")]
    public async Task Tiff_output_stays_compressed(string operationId, string name)
    {
        var input = _dir.Combine(name);
        using (var pages = new MagickImageCollection())
        {
            pages.Add(new MagickImage(MagickColors.White, 2000, 1500));
            pages.Add(new MagickImage(MagickColors.LightGray, 2000, 1500));
            foreach (var page in pages)
                page.Settings.Compression = CompressionMethod.Zip;
            if (name.EndsWith(".tiff", StringComparison.Ordinal))
                pages.Write(input, MagickFormat.Tiff);
            else
                pages[0].Write(input, MagickFormat.Png);
        }

        var output = await Op(operationId).RunAsync(input, CancellationToken.None);

        // Uncompressed, one 2000x1500 RGB page alone is 9 MB.
        Assert.True(new FileInfo(output).Length < 500_000, $"{new FileInfo(output).Length} bytes from {new FileInfo(input).Length}");
    }

    [Fact]
    public async Task Removing_metadata_keeps_the_quality_of_a_jpeg_compressed_tiff()
    {
        // The TIFF reader reports quality 0, so carrying the JPEG compression over re-encoded at libtiff's default (~75).
        var input = _dir.Combine("scan.tiff");
        using (var image = new MagickImage(MagickColors.SteelBlue, 600, 400))
        {
            image.AddNoise(NoiseType.Gaussian, 0.3);
            image.Settings.Compression = CompressionMethod.JPEG;
            image.Quality = 95;
            image.Write(input, MagickFormat.Tiff);
        }

        var output = await Op("image.strip-metadata").RunAsync(input, CancellationToken.None);

        using var before = new MagickImage(input);
        using var after = new MagickImage(output);
        var error = before.Compare(after, ErrorMetric.RootMeanSquared);
        Assert.True(error < 0.005, $"RMS error {error:F4}");
    }

    [Fact]
    public async Task Converting_to_tiff_compresses_losslessly()
    {
        // An 8 KB PNG of 8000x8000 became an 8 MB TIFF: the TIFF writer defaults to no compression.
        var input = _dir.Combine("flat.png");
        using (var image = new MagickImage(MagickColors.White, 3000, 3000))
            image.Write(input, MagickFormat.Png);

        var output = await Op("image.to-tiff").RunAsync(input, CancellationToken.None);

        Assert.True(new FileInfo(output).Length < 500_000, $"{new FileInfo(output).Length} bytes");
        using var result = new MagickImage(output);
        Assert.Equal(0, result.Compare(new MagickImage(input), ErrorMetric.Absolute));
    }

    // A 300 dpi A4 page is 8.7 MP: a 400 MP cap on all pages refused scans from about 47 pages, although G4 or LZW pages
    // encode to a few MB. Whether the output is compressed proves nothing either: a 2.2 MB crafted TIFF of Deflate pages
    // produced 435 MB. What bounds the in-memory encode is the worst-case raw size: a bilevel page counts 1/24 of a colour one.
    [Theory]
    [InlineData(1u, 1u, true)]   // 60 black-and-white A4 pages: 65 MB raw
    [InlineData(3u, 8u, false)]  // 60 colour A4 pages: 1.5 GB raw
    public void Many_pages_are_judged_by_their_worst_case_raw_size(uint channels, uint depth, bool fits)
    {
        var page = (Width: 2480UL, Height: 3508UL);
        var sixtyPages = Enumerable.Repeat(page.Width * page.Height * channels * depth / 8, 60).Aggregate(0UL, (a, b) => a + b);

        Assert.Equal(fits, ImageFile.FitsInMemory(sixtyPages));
        Assert.True(ImageFile.FitsInMemory(100_000_000), "small totals always fit");
    }

    [Fact]
    public async Task Removing_metadata_keeps_the_colour_page_of_a_mixed_scan_in_colour()
    {
        // Page 1 Group 4 (black and white), page 2 colour LZW. One compression for all pages made page 2 bilevel.
        var input = _dir.Combine("scan.tif");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "mixed-g4-colour.tif"), input);

        var output = await Op("image.strip-metadata").RunAsync(input, CancellationToken.None);

        using var pages = new MagickImageCollection(output);
        Assert.Equal(2, pages.Count);
        var red = pages[1].GetPixels().GetPixel(100, 75).ToColor()!;
        Assert.True(red.R > 200 && red.G < 80 && red.B < 80, $"page 2 should still be red, was {red}");
    }

    // A reduced-resolution thumbnail stored as a second image is not a second page.
    [Theory]
    [InlineData("image.compress-balanced")]
    [InlineData("image.compress-under-1mb")]
    public async Task A_tiff_with_a_thumbnail_is_not_treated_as_multi_page(string operationId)
    {
        var input = _dir.Combine("photo.tif");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "with-thumbnail.tif"), input);

        var output = await Op(operationId).RunAsync(input, CancellationToken.None);

        using var jpg = new MagickImage(output);
        Assert.Equal((300u, 200u), (jpg.Width, jpg.Height));
    }

    [Fact]
    public async Task Compress_on_a_multi_page_tiff_fails_with_a_clear_reason_instead_of_dropping_pages()
    {
        // Compress makes one image: that cannot fulfil the request, so it is a failure, not "nothing to gain".
        var input = _dir.Combine("scan.tiff");
        using (var pages = new MagickImageCollection())
        {
            pages.Add(new MagickImage(MagickColors.Red, 64, 64));
            pages.Add(new MagickImage(MagickColors.Blue, 64, 64));
            pages.Add(new MagickImage(MagickColors.Green, 64, 64));
            pages.Write(input, MagickFormat.Tiff);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Op("image.compress-balanced").RunAsync(input, CancellationToken.None));

        Assert.Contains("pages", error.Message, StringComparison.Ordinal);
        Assert.Equal(["scan.tiff"], _dir.FileNames());
    }

    [Fact]
    public async Task Compress_under_n_on_a_multi_page_tiff_fails_instead_of_dropping_pages()
    {
        var input = _dir.Combine("scan.tiff");
        using (var pages = new MagickImageCollection())
        {
            pages.Add(new MagickImage(MagickColors.Red, 64, 64));
            pages.Add(new MagickImage(MagickColors.Blue, 64, 64));
            pages.Write(input, MagickFormat.Tiff);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Op("image.compress-under-1mb").RunAsync(input, CancellationToken.None));

        Assert.Contains("pages", error.Message, StringComparison.Ordinal);
    }

    // GIF pixels are only re-optimised, never made lossier: when that saves too little, "already well compressed" would
    // be false for a 10 MB GIF that is simply too large to send.
    [Fact]
    public async Task Compress_says_honestly_when_it_cannot_make_an_animated_gif_smaller()
    {
        var input = _dir.Combine("clip.gif");
        using (var frames = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
                frames.Add(new MagickImage(color, 32, 32) { AnimationDelay = 10 });
            frames.Optimize();
            frames.Write(input, MagickFormat.Gif);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Op("image.compress-strong").RunAsync(input, CancellationToken.None));

        Assert.Contains("Convert > WEBP", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_metadata_keeps_every_page_of_a_tiff()
    {
        var output = await Op("image.strip-metadata").RunAsync(ThreePageTiff(), CancellationToken.None);

        using var result = new MagickImageCollection(output);
        Assert.Equal(3, result.Count);
    }

    // Pages of a scan are not frames of an animation: turning them into a GIF that flips through them with no delay
    // is not what "Convert > GIF" means.
    [Theory]
    [InlineData("image.to-gif")]
    [InlineData("image.to-webp")]
    public async Task A_multi_page_tiff_converts_to_its_first_page_not_to_an_animation(string operationId)
    {
        var output = await Op(operationId).RunAsync(ThreePageTiff(), CancellationToken.None);

        using var result = new MagickImageCollection(output);
        Assert.Single(result);
        var pixel = result[0].GetPixels().GetPixel(16, 16).ToColor()!;
        Assert.True(pixel.R > 200 && pixel.G < 60, $"first page should be red, was {pixel}");
    }
}
