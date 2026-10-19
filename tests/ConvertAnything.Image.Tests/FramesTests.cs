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
