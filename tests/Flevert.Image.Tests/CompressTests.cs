using Flevert.Core;
using ImageMagick;
using ImageMagick.Drawing;

namespace Flevert.Image.Tests;

public sealed class CompressTests : IDisposable
{
    private static readonly string Heic = Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string level) => ImageOperations.All.Single(op => op.Id == $"image.compress-{level}");

    [Theory]
    [InlineData("light", "Compress/Light (best quality)")]
    [InlineData("balanced", "Compress/Balanced")]
    [InlineData("strong", "Compress/Strong (smallest)")]
    public void Each_level_is_offered_for_every_readable_image(string level, string menuPath)
    {
        var op = Op(level);

        Assert.Equal(menuPath, op.MenuPath);
        Assert.Equal(ImageFile.ReadableExtensions.Order(StringComparer.Ordinal), op.Extensions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Balanced_makes_a_12_megapixel_camera_original_at_least_40_percent_smaller()
    {
        // Like a phone photo: smooth areas with mild sensor noise, saved at quality 95 with 4:2:0 chroma subsampling
        // (a 4:4:4 noise image would shrink far more than real photos do).
        var input = CameraLike("photo.jpg", 4000, 3000, quality: 95);

        var output = await Op("balanced").RunAsync(input, CancellationToken.None);

        Assert.Equal("photo (compressed).jpg", Path.GetFileName(output));
        var ratio = (double)new FileInfo(output).Length / new FileInfo(input).Length;
        Assert.True(ratio <= 0.6, $"output is {ratio:P0} of the input");
    }

    // A JPG already saved at quality 70 (a chat download) or a tiny PNG gets bigger when re-encoded; writing that as
    // "compressed" would mislead.
    // Skipped, not failed: nothing went wrong, there is just nothing worth saving. A q80 source at Light (q85) saves a
    // fraction of a percent after a lossy re-encode, which is not worth a second copy either.
    [Theory]
    [InlineData("light", "chat.jpg", 70u)]
    [InlineData("light", "shared.jpg", 80u)]
    [InlineData("strong", "icon.png", null)]
    public async Task A_result_that_is_not_meaningfully_smaller_is_skipped_and_not_written(string level, string name, uint? quality)
    {
        var input = name.EndsWith(".png", StringComparison.Ordinal) ? Flat(name) : CameraLike(name, 1200, 900, quality!.Value);

        var skipped = await Assert.ThrowsAsync<OperationSkippedException>(() => Op(level).RunAsync(input, CancellationToken.None));

        Assert.Contains("already well compressed", skipped.Message, StringComparison.Ordinal);
        Assert.Equal([name], _dir.FileNames());
    }

    [Fact]
    public async Task Balanced_writes_jpg_with_4_2_0_chroma_subsampling()
    {
        var input = Photo("hq.jpg", 800, 600, quality: 98);
        using (var source = new MagickImage(input))
            Assert.StartsWith("1x1", source.GetAttribute("jpeg:sampling-factor"), StringComparison.Ordinal); // control: 4:4:4

        var output = await Op("balanced").RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.StartsWith("2x2", result.GetAttribute("jpeg:sampling-factor"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strong_also_limits_the_long_edge_to_2560_pixels()
    {
        var input = Photo("photo.jpg", 4000, 3000, quality: 95);

        var output = await Op("strong").RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.Equal((2560u, 1920u), (result.Width, result.Height));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("balanced")]
    public async Task Light_and_balanced_keep_a_png_pixel_for_pixel(string level)
    {
        var input = Photo("chart.png", 300, 200, quality: null);

        var output = await Op(level).RunAsync(input, CancellationToken.None);

        Assert.Equal("chart (compressed).png", Path.GetFileName(output));
        using var before = new MagickImage(input);
        using var after = new MagickImage(output);
        Assert.Equal(0, before.Compare(after, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task Strong_reduces_a_png_to_at_most_256_colours()
    {
        var input = Photo("chart.png", 300, 200, quality: null);
        using (var source = new MagickImage(input))
            Assert.True(source.TotalColors > 256, "control: the source needs more than 256 colours");

        var output = await Op("strong").RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, result.Format);
        Assert.True(result.TotalColors <= 256, $"{result.TotalColors} colours");
    }

    [Theory]
    [InlineData("IMG_0001.HEIC")]
    [InlineData("scan.bmp")]
    [InlineData("scan.tiff")]
    public async Task Formats_that_cannot_be_compressed_in_place_become_jpg(string name)
    {
        var input = _dir.Combine(name);
        if (name.EndsWith(".HEIC", StringComparison.Ordinal))
            File.Copy(Heic, input);
        else
            Photo(name, 64, 48, quality: null);

        var output = await Op("balanced").RunAsync(input, CancellationToken.None);

        Assert.Equal(Path.GetFileNameWithoutExtension(name) + " (compressed).jpg", Path.GetFileName(output));
        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, result.Format);
    }

    [Fact]
    public async Task An_animated_gif_stays_an_animated_gif()
    {
        var input = _dir.Combine("anim.gif");
        using (var animation = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
                animation.Add(new MagickImage(color, 16, 16) { AnimationDelay = 10 });
            // GIF pixels are not re-compressed; only removing metadata makes it smaller, so give it some.
            animation[0].Comment = new string('x', 10_000);
            animation.Write(input, MagickFormat.Gif);
        }

        var output = await Op("balanced").RunAsync(input, CancellationToken.None);

        Assert.Equal("anim (compressed).gif", Path.GetFileName(output));
        using var result = new MagickImageCollection(output);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task Metadata_is_removed_except_the_colour_profile()
    {
        var input = _dir.Combine("photo.jpg");
        using (var image = new MagickImage(MagickColors.Gray, 32, 32))
        {
            image.SetProfile(ColorProfiles.SRGB);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            image.SetProfile(exif);
            image.SetAttribute("comment", "secret " + new string('x', 20_000)); // also enough to gain over 5%
            image.Write(input, MagickFormat.Jpeg);
        }

        var output = await Op("balanced").RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.Null(result.GetExifProfile());
        Assert.Null(result.GetXmpProfile());
        Assert.Null(result.GetIptcProfile());
        Assert.Null(result.Comment);
        Assert.NotNull(result.GetColorProfile());
    }

    /// <summary>Smooth shapes, blurred, with mild noise, as JPG with 4:2:0 subsampling: sizes behave like camera output.</summary>
    private string CameraLike(string name, uint width, uint height, uint quality)
    {
        var path = _dir.Combine(name);
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);
        image.Draw(new Drawables()
            .FillColor(MagickColors.Orange).Ellipse(width / 3.0, height / 2.0, width / 4.0, height / 3.0, 0, 360)
            .FillColor(MagickColors.ForestGreen).Rectangle(width * 0.6, height * 0.2, width * 0.9, height * 0.8)
            .FillColor(MagickColors.Beige).Circle(width * 0.8, height * 0.85, width * 0.75, height * 0.85));
        image.GaussianBlur(0, 6);
        image.AddNoise(NoiseType.Gaussian, 0.15);
        image.Quality = quality;
        image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private string Flat(string name)
    {
        var path = _dir.Combine(name);
        // Already optimised, like an icon from an optimiser: no text chunks, maximum zlib level.
        using var image = new MagickImage(MagickColors.Red, 1, 1);
        image.Strip();
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        image.Write(path, MagickFormat.Png);
        return path;
    }

    /// <summary>A gradient with noise: compresses like a photo, with many distinct colours.</summary>
    private string Photo(string name, uint width, uint height, uint? quality)
    {
        var path = _dir.Combine(name);
        using var image = new MagickImage(MagickColors.SteelBlue, width, height);
        image.Draw(new Drawables()
            .FillColor(MagickColors.Orange).Ellipse(width / 3.0, height / 2.0, width / 4.0, height / 3.0, 0, 360)
            .FillColor(MagickColors.ForestGreen).Rectangle(width * 0.6, height * 0.2, width * 0.9, height * 0.8));
        image.AddNoise(NoiseType.Gaussian, 0.6);
        if (quality is { } q)
            image.Quality = q;
        // A PNG as most tools write it, at a modest zlib level, so maximum compression has something to gain.
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
        image.Write(path, ImageFile.FormatOf(Path.GetExtension(name)));
        return path;
    }
}
