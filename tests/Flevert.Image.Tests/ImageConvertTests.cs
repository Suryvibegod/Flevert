using ImageMagick;

namespace Flevert.Image.Tests;

public sealed class ImageConvertTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static Flevert.Core.IOperation ToPng() => ImageOperations.All.Single(op => op.Id == "image.to-png");

    [Fact]
    public void To_png_sits_under_convert_and_is_offered_for_every_readable_type_except_png()
    {
        var op = ToPng();

        Assert.Equal("Convert to/PNG", op.MenuPath);
        Assert.Equal(
            ImageFile.ReadableExtensions.Where(e => e != ".png").Order(StringComparer.Ordinal),
            op.Extensions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void To_gif_is_not_offered_for_heic_or_heif_photos()
    {
        // 256 colours ruin a photo, and the entry would push the menu of these types past the 16 entries Explorer shows
        // (docs/specs/image.md).
        var op = ImageOperations.All.Single(op => op.Id == "image.to-gif");

        Assert.Equal(
            ImageFile.ReadableExtensions.Where(e => e is not (".gif" or ".heic" or ".heif" or ".bmp")).Order(StringComparer.Ordinal),
            op.Extensions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Jpg_stored_sideways_with_exif_orientation_6_becomes_an_upright_png_beside_it()
    {
        // Stored 40×20: left half red, right half blue. Orientation 6 means "rotate 90° clockwise to display", so the
        // upright image is 20×40 with red on top.
        var input = _dir.Combine("photo.jpg");
        using (var stored = new MagickImage(MagickColors.Blue, 40, 20))
        {
            stored.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 19, 19));
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            stored.SetProfile(exif);
            // ImageMagick rewrites the EXIF tag from this property when it saves.
            stored.Orientation = Orientation.RightTop;
            stored.Write(input, MagickFormat.Jpeg);
        }

        using (var check = new MagickImage(input))
            Assert.Equal((ushort)6, check.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);

        var before = await File.ReadAllBytesAsync(input);

        var output = await ToPng().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("photo.png"), output);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], (await File.ReadAllBytesAsync(output))[..4]);
        using var png = new MagickImage(output);
        Assert.Equal((20u, 40u), (png.Width, png.Height));
        Assert.True(IsMostly(png, 10, 5, MagickColors.Red), "top should be red");
        Assert.True(IsMostly(png, 10, 35, MagickColors.Blue), "bottom should be blue");
        Assert.Equal(before, await File.ReadAllBytesAsync(input));
    }

    [Fact]
    public async Task A_jpg_holding_a_script_is_refused_and_leaves_no_output()
    {
        var disguised = _dir.File("photo.jpg", "push graphic-context\nviewbox 0 0 8 8\npop graphic-context\n");

        await Assert.ThrowsAnyAsync<MagickException>(() => ToPng().RunAsync(disguised, CancellationToken.None));

        Assert.Equal(["photo.jpg"], _dir.FileNames());
    }

    [Fact]
    public async Task A_png_saved_as_jpg_still_converts()
    {
        var misnamed = _dir.Combine("download.jpg");
        using (var image = new MagickImage(MagickColors.Red, 8, 8))
            image.Write(misnamed, MagickFormat.Png);

        var output = await ImageOperations.All.Single(op => op.Id == "image.to-webp").RunAsync(misnamed, CancellationToken.None);

        using var webp = new MagickImage(output);
        Assert.Equal(MagickFormat.WebP, webp.Format);
    }

    // The runner passes the pinned handle's final path (\\?\C:\…). ImageMagick's filename parser gives meaning to "[n]"
    // (frame), "%d" (scene number), "~" (home), "{a,b}" and "@" (file list) if a name ever reaches it.
    [Theory]
    [InlineData("ảnh[0] x.jpg")]
    [InlineData("scan%d.jpg")]
    [InlineData("~photo.jpg")]
    [InlineData("a{b,c}.jpg")]
    [InlineData("@list.jpg")]
    [InlineData("#1.jpg")]
    public async Task Names_imagemagick_would_parse_and_extended_length_paths_are_read_as_plain_files(string name)
    {
        var input = _dir.Combine(name);
        using (var image = new MagickImage(MagickColors.Green, 8, 6))
        {
            using var file = File.Create(input);
            image.Write(file, MagickFormat.Jpeg);
        }

        var output = await ToPng().RunAsync((OperatingSystem.IsWindows() ? @"\\?\" : "") + input, CancellationToken.None);

        Assert.Equal(Path.ChangeExtension(name, ".png"), Path.GetFileName(output));
        using var png = new MagickImage(output);
        Assert.Equal((8u, 6u), (png.Width, png.Height));
    }

    private static bool IsMostly(MagickImage image, int x, int y, MagickColor expected)
    {
        var actual = image.GetPixels().GetPixel(x, y).ToColor()!;
        return Math.Abs(actual.R - expected.R) < 40 && Math.Abs(actual.G - expected.G) < 40 && Math.Abs(actual.B - expected.B) < 40;
    }
}
