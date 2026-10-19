using ConvertAnything.Core;
using ImageMagick;
using ImageMagick.Drawing;

namespace ConvertAnything.Image.Tests;

public sealed class CompressTests : IDisposable
{
    private static readonly string Heic = Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string level) => ImageOperations.All.Single(op => op.Id == $"image.compress-{level}");

    [Theory]
    [InlineData("light", "Compress/Light")]
    [InlineData("balanced", "Compress/Balanced")]
    [InlineData("strong", "Compress/Strong")]
    public void Each_level_is_offered_for_every_readable_image(string level, string menuPath)
    {
        var op = Op(level);

        Assert.Equal(menuPath, op.MenuPath);
        Assert.Equal(ImageFile.ReadableExtensions.Order(StringComparer.Ordinal), op.Extensions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Balanced_makes_a_12_megapixel_photo_at_least_40_percent_smaller()
    {
        var input = Photo("photo.jpg", 4000, 3000, quality: 95);

        var output = await Op("balanced").RunAsync(input, CancellationToken.None);

        Assert.Equal("photo (compressed).jpg", Path.GetFileName(output));
        var ratio = (double)new FileInfo(output).Length / new FileInfo(input).Length;
        Assert.True(ratio <= 0.6, $"output is {ratio:P0} of the input");
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
            image.SetAttribute("comment", "secret");
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
        image.Write(path, ImageFile.FormatOf(Path.GetExtension(name)));
        return path;
    }
}
