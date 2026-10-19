using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image.Tests;

public sealed class CompressUnderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string id) => ImageOperations.All.Single(op => op.Id == id);

    [Theory]
    [InlineData("image.compress-under-500kb", "Compress/Under 500 KB")]
    [InlineData("image.compress-under-1mb", "Compress/Under 1 MB")]
    [InlineData("image.compress-under-5mb", "Compress/Under 5 MB")]
    public void Each_size_is_offered_for_every_readable_image(string id, string menuPath)
    {
        var op = Op(id);

        Assert.Equal(menuPath, op.MenuPath);
        Assert.Equal(ImageFile.ReadableExtensions.Order(StringComparer.Ordinal), op.Extensions.Order(StringComparer.Ordinal));
    }

    // Noise is the worst case for JPEG: even at quality 40 a 12 MP noise image is several megabytes, so these sizes are
    // only reached by also shrinking it.
    [Theory]
    [InlineData("image.compress-under-1mb", 1_000_000, "photo (under 1 MB).jpg")]
    [InlineData("image.compress-under-500kb", 500_000, "photo (under 500 KB).jpg")]
    public async Task A_noisy_12_megapixel_image_ends_up_below_the_limit(string id, long limit, string expectedName)
    {
        var input = Noise("photo.png", 4000, 3000);

        var output = await Op(id).RunAsync(input, CancellationToken.None);

        Assert.Equal(expectedName, Path.GetFileName(output));
        Assert.True(new FileInfo(output).Length < limit, $"{new FileInfo(output).Length} bytes");
        using var jpg = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, jpg.Format);
    }

    [Fact]
    public async Task An_image_already_below_the_limit_becomes_a_jpg_of_the_same_size_in_pixels()
    {
        var input = Noise("small.png", 200, 100);

        var output = await Op("image.compress-under-5mb").RunAsync(input, CancellationToken.None);

        using var jpg = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, jpg.Format);
        Assert.Equal((200u, 100u), (jpg.Width, jpg.Height));
    }

    [Fact]
    public async Task The_best_quality_that_fits_is_used_rather_than_the_lowest()
    {
        // 1200×900 noise fits under 1 MB well above quality 40; a search that settled for 40 would waste the budget.
        var input = Noise("mid.png", 1200, 900);

        var output = await Op("image.compress-under-1mb").RunAsync(input, CancellationToken.None);

        using var jpg = new MagickImage(output);
        Assert.Equal((1200u, 900u), (jpg.Width, jpg.Height));
        Assert.True(jpg.Quality > 40, $"quality {jpg.Quality}");
    }

    private string Noise(string name, uint width, uint height)
    {
        var path = _dir.Combine(name);
        using var image = new MagickImage(MagickColors.Gray, width, height);
        image.AddNoise(NoiseType.Random);
        image.Write(path, MagickFormat.Png);
        return path;
    }
}
