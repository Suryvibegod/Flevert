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
                catch (ConvertAnything.Core.OperationSkippedException)
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
