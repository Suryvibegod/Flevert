using Flevert.Core;
using ImageMagick;
using ImageMagick.Drawing;

namespace Flevert.Image.Tests;

public sealed class ConvertMatrixTests : IDisposable
{
    private static readonly string Heic = Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string, string> Pairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var op in ImageOperations.All.Where(op => op.Id.StartsWith("image.to-", StringComparison.Ordinal) && op.Id != "image.to-text"))
        {
            foreach (var source in op.Extensions)
                data.Add(source, op.Id);
        }

        return data;
    }

    [Fact]
    public void Every_target_format_in_the_spec_is_offered()
    {
        Assert.Equal(
            ["image.to-avif", "image.to-gif", "image.to-jpg", "image.to-png", "image.to-text", "image.to-tiff", "image.to-webp"],
            ImageOperations.All.Select(op => op.Id).Where(id => id.StartsWith("image.to-", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task Every_source_converts_to_every_other_format(string sourceExtension, string operationId)
    {
        var input = Source(sourceExtension);
        var op = ImageOperations.All.Single(o => o.Id == operationId);

        var output = await op.RunAsync(input, CancellationToken.None);

        var target = operationId["image.to-".Length..];
        Assert.Equal("." + target, Path.GetExtension(output));
        Assert.True(HasSignature(await File.ReadAllBytesAsync(output), target), $"{output} does not start like a {target} file");
    }

    [Fact]
    public async Task Transparent_areas_become_white_where_the_target_has_no_alpha()
    {
        var input = _dir.Combine("logo.png");
        using (var image = new MagickImage(MagickColors.Transparent, 20, 20))
        {
            image.Draw(new Drawables().FillColor(MagickColors.Black).Rectangle(10, 0, 19, 19));
            image.Write(input, MagickFormat.Png);
        }

        var output = await Op("image.to-jpg").RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        var transparentSide = result.GetPixels().GetPixel(2, 10).ToColor()!;
        Assert.True(transparentSide.R > 240 && transparentSide.G > 240 && transparentSide.B > 240, $"was {transparentSide}");
    }

    [Theory]
    [InlineData("image.to-webp", 3)]
    [InlineData("image.to-gif", 3)]
    [InlineData("image.to-png", 1)]
    public async Task Animation_is_kept_for_gif_and_webp_and_other_targets_take_the_first_frame(string operationId, int frames)
    {
        var input = _dir.Combine("anim.gif");
        using (var animation = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
            {
                var frame = new MagickImage(color, 16, 16) { AnimationDelay = 10 };
                animation.Add(frame);
            }

            animation.Write(input, MagickFormat.Gif);
        }

        var output = await Op(operationId).RunAsync(input, CancellationToken.None);

        using var result = new MagickImageCollection(output);
        Assert.Equal(frames, result.Count);
        var first = result[0].GetPixels().GetPixel(8, 8).ToColor()!;
        Assert.True(first.R > 200 && first.G < 60, $"first frame should be red, was {first}");
    }

    [Fact]
    public async Task Heic_from_an_iphone_becomes_a_jpg_of_the_same_size()
    {
        var input = _dir.Combine("IMG_0001.HEIC");
        File.Copy(Heic, input);

        var output = await Op("image.to-jpg").RunAsync(input, CancellationToken.None);

        Assert.Equal("IMG_0001.jpg", Path.GetFileName(output));
        using var jpg = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, jpg.Format);
        Assert.Equal((320u, 240u), (jpg.Width, jpg.Height));
    }

    // Convert keeps metadata (Remove metadata is the operation for stripping it). TIFF is left out: ImageMagick's TIFF
    // writer drops the EXIF block (checked by hand), a limitation recorded in docs/specs/image.md.
    [Theory]
    [InlineData("image.to-png")]
    [InlineData("image.to-webp")]
    [InlineData("image.to-avif")]
    public async Task Converting_keeps_exif_where_the_target_can_hold_it(string operationId)
    {
        var input = _dir.Combine("photo.jpg");
        using (var image = new MagickImage(MagickColors.Gray, 16, 16))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            image.SetProfile(exif);
            image.Write(input, MagickFormat.Jpeg);
        }

        var output = await Op(operationId).RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.Equal("TestCam", result.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }

    // At quality >= 90 ImageMagick writes 4:4:4 unless told; an iPhone photo turned into JPG grew to 144% that way.
    [Theory]
    [InlineData("image.to-jpg")]
    [InlineData("image.strip-metadata")]
    public async Task Heic_becomes_a_jpg_with_4_2_0_subsampling(string operationId)
    {
        var input = _dir.Combine("IMG_0002.HEIC");
        File.Copy(Heic, input);

        var output = await Op(operationId).RunAsync(input, CancellationToken.None);

        using var jpg = new MagickImage(output);
        Assert.StartsWith("2x2", jpg.GetAttribute("jpeg:sampling-factor"), StringComparison.Ordinal);
    }

    // Lossy WebP is always 4:2:0; written as JPG at q92 it became 4:4:4 and grew from 131 KB to 441 KB.
    [Fact]
    public async Task Lossy_webp_becomes_a_jpg_with_4_2_0_subsampling()
    {
        var input = _dir.Combine("download.webp");
        using (var image = new MagickImage(MagickColors.SteelBlue, 200, 150))
        {
            image.AddNoise(NoiseType.Gaussian, 0.3);
            image.Quality = 80;
            image.Write(input, MagickFormat.WebP);
        }

        var output = await Op("image.to-jpg").RunAsync(input, CancellationToken.None);

        using var jpg = new MagickImage(output);
        Assert.StartsWith("2x2", jpg.GetAttribute("jpeg:sampling-factor"), StringComparison.Ordinal);
    }

    private static IOperation Op(string id) => ImageOperations.All.Single(op => op.Id == id);

    private string Source(string extension)
    {
        var path = _dir.Combine("source" + extension);
        var format = ImageFile.FormatOf(extension);
        if (format is MagickFormat.Heic or MagickFormat.Heif)
        {
            File.Copy(Heic, path);
            return path;
        }

        using var image = new MagickImage(MagickColors.SkyBlue, 64, 48);
        image.Draw(new Drawables().FillColor(MagickColors.OrangeRed).Rectangle(0, 0, 31, 23));
        image.Write(path, format);
        return path;
    }

    private static bool HasSignature(byte[] bytes, string target) => target switch
    {
        "jpg" => bytes is [0xFF, 0xD8, 0xFF, ..],
        "png" => bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..],
        "gif" => bytes is [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..],
        "bmp" => bytes is [(byte)'B', (byte)'M', ..],
        "tiff" => bytes is [(byte)'I', (byte)'I', 42, 0, ..] or [(byte)'M', (byte)'M', 0, 42, ..],
        "webp" => bytes.Length > 12 && bytes is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', ..]
            && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP",
        "avif" => bytes.Length > 12 && System.Text.Encoding.ASCII.GetString(bytes, 4, 8) is "ftypavif" or "ftypavis",
        _ => false,
    };
}
