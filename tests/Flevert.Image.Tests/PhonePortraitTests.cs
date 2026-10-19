using ImageMagick;
using Flevert.Core;

namespace Flevert.Image.Tests;

// A portrait photo as an iPhone writes it: pixels stored 320x240 with a HEIF rotation (irot) and EXIF Orientation 6 both
// present, plus GPS and camera details. Upright it is 240x320 with a white block at the top-left.
public sealed class PhonePortraitTests : IDisposable
{
    private static readonly string Portrait = Path.Combine(AppContext.BaseDirectory, "fixtures", "portrait-gps.heic");

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string id) => ImageOperations.All.Single(op => op.Id == id);

    [Theory]
    [InlineData("image.to-jpg")]
    [InlineData("image.strip-metadata")]
    public async Task A_portrait_phone_photo_comes_out_upright_and_turned_only_once(string id)
    {
        var input = _dir.Combine("IMG_0001.HEIC");
        File.Copy(Portrait, input);

        var output = await Op(id).RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.Equal((240u, 320u), (result.Width, result.Height));
        Assert.True(IsWhite(result, 50, 40), "the white block should be at the top-left");
        Assert.False(IsWhite(result, 190, 280), "the bottom-right should not be white");
        // Upright pixels plus a leftover Orientation 6 would make viewers turn the picture a second time.
        var orientation = result.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value;
        Assert.True(orientation is null or 1, $"EXIF Orientation should be absent or 1, was {orientation}");
    }

    [Fact]
    public async Task Removing_metadata_from_a_phone_photo_drops_its_location_and_camera()
    {
        var input = _dir.Combine("IMG_0001.HEIC");
        File.Copy(Portrait, input);
        // Control: the input really carries the details the test expects to disappear.
        using (var source = new MagickImage(input))
            Assert.NotNull(source.GetExifProfile()!.GetValue(ExifTag.GPSLatitude));
        Assert.Contains("SecretPhoneMake", Latin1(input), StringComparison.Ordinal);

        var output = await Op("image.strip-metadata").RunAsync(input, CancellationToken.None);

        using (var result = new MagickImage(output))
            Assert.Null(result.GetExifProfile()?.GetValue(ExifTag.GPSLatitude));
        var bytes = Latin1(output);
        Assert.DoesNotContain("SecretPhoneMake", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretPhoneModel", bytes, StringComparison.Ordinal);
    }

    private static string Latin1(string path) => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));

    private static bool IsWhite(MagickImage image, int x, int y)
    {
        var c = image.GetPixels().GetPixel(x, y).ToColor()!;
        return c.R > 230 && c.G > 230 && c.B > 230;
    }
}
