using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image.Tests;

public sealed class StripMetadataTests : IDisposable
{
    private static readonly string Heic = Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op() => ImageOperations.All.Single(op => op.Id == "image.strip-metadata");

    [Fact]
    public void Remove_metadata_is_offered_for_every_readable_image()
    {
        Assert.Equal("Remove metadata", Op().MenuPath);
        Assert.Equal(ImageFile.ReadableExtensions.Order(StringComparer.Ordinal), Op().Extensions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Location_camera_and_other_metadata_are_removed_but_colours_and_orientation_survive()
    {
        var input = _dir.Combine("IMG_2024.jpg");
        using (var image = new MagickImage(MagickColors.Gray, 40, 20))
        {
            image.SetProfile(ColorProfiles.SRGB);
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "TestCam");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.Orientation, (ushort)6); // ImageMagick only syncs Orientation into an existing tag
            image.SetProfile(exif);
            var iptc = new IptcProfile();
            iptc.SetValue(IptcTag.Caption, "secret caption");
            image.SetProfile(iptc);
            image.SetProfile(new XmpProfile(System.Text.Encoding.UTF8.GetBytes(
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta>")));
            image.Comment = "secret comment";
            image.Orientation = Orientation.RightTop;
            image.Write(input, MagickFormat.Jpeg);
        }

        using (var check = new MagickImage(input))
        {
            Assert.Equal((ushort)6, check.GetExifProfile()!.GetValue(ExifTag.Orientation)!.Value);
            Assert.NotNull(check.GetIptcProfile());
            Assert.NotNull(check.GetXmpProfile());
        }

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal("IMG_2024 (no metadata).jpg", Path.GetFileName(output));
        using var result = new MagickImage(output);
        Assert.Null(result.GetExifProfile());
        Assert.Null(result.GetXmpProfile());
        Assert.Null(result.GetIptcProfile());
        Assert.Null(result.Comment);
        Assert.NotNull(result.GetColorProfile());
        Assert.Equal((20u, 40u), (result.Width, result.Height)); // rotated, since the orientation tag is gone
    }

    [Theory]
    [InlineData("chart.png", "chart (no metadata).png")]
    [InlineData("pic.webp", "pic (no metadata).webp")]
    [InlineData("IMG_0001.HEIC", "IMG_0001 (no metadata).jpg")]
    public async Task The_format_is_kept_except_for_heic_which_cannot_be_written(string name, string expected)
    {
        var input = _dir.Combine(name);
        if (name.EndsWith(".HEIC", StringComparison.Ordinal))
        {
            File.Copy(Heic, input);
        }
        else
        {
            using var image = new MagickImage(MagickColors.Teal, 16, 16);
            image.Write(input, ImageFile.FormatOf(Path.GetExtension(name)));
        }

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(expected, Path.GetFileName(output));
    }

    // Removing metadata must not cost visible quality: the re-encode has to beat Compress > Light on the same file.
    [Theory]
    [InlineData(".webp", MagickFormat.WebP)]
    [InlineData(".avif", MagickFormat.Avif)]
    [InlineData(".jpg", MagickFormat.Jpeg)]
    public async Task Removing_metadata_keeps_more_quality_than_light_compression(string extension, MagickFormat format)
    {
        var input = _dir.Combine("photo" + extension);
        using (var image = new MagickImage(MagickColors.SteelBlue, 600, 400))
        {
            image.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Orange).Ellipse(200, 200, 150, 120, 0, 360));
            image.GaussianBlur(0, 4);
            image.AddNoise(NoiseType.Gaussian, 0.3);
            image.Quality = 95;
            image.Write(input, format);
        }

        var stripped = await Op().RunAsync(input, CancellationToken.None);
        var light = await ImageOperations.All.Single(op => op.Id == "image.compress-light").RunAsync(input, CancellationToken.None);

        using var source = new MagickImage(input);
        using var afterStrip = new MagickImage(stripped);
        using var afterLight = new MagickImage(light);
        var stripError = source.Compare(afterStrip, ErrorMetric.RootMeanSquared);
        var lightError = source.Compare(afterLight, ErrorMetric.RootMeanSquared);
        Assert.True(stripError < lightError, $"strip error {stripError:F5}, light error {lightError:F5}");
    }

    // ImageMagick's JPEG writer picks 4:4:4 at quality >= 90 unless told otherwise, so a 4:2:0 phone photo grew by
    // 8-16%, and a 4:4:4 source at q80 lost chroma detail.
    [Theory]
    [InlineData("4:4:4", 80u, "1x1")]
    [InlineData("4:2:0", 95u, "2x2")]
    public async Task A_jpg_keeps_its_chroma_subsampling(string sampling, uint quality, string expected)
    {
        var input = _dir.Combine("photo.jpg");
        using (var image = new MagickImage(MagickColors.SteelBlue, 200, 150))
        {
            image.AddNoise(NoiseType.Gaussian, 0.3);
            image.Quality = quality;
            image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", sampling);
            image.Write(input, MagickFormat.Jpeg);
        }

        using (var check = new MagickImage(input))
            Assert.StartsWith(expected, check.GetAttribute("jpeg:sampling-factor"), StringComparison.Ordinal);

        var output = await Op().RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.StartsWith(expected, result.GetAttribute("jpeg:sampling-factor"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lossless_webp_stays_lossless()
    {
        var input = _dir.Combine("art.webp");
        using (var image = new MagickImage(MagickColors.Purple, 64, 64))
        {
            image.AddNoise(NoiseType.Random);
            image.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
            image.Write(input, MagickFormat.WebP);
        }

        var output = await Op().RunAsync(input, CancellationToken.None);

        using var before = new MagickImage(input);
        using var after = new MagickImage(output);
        Assert.Equal(0, before.Compare(after, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task An_animated_lossless_webp_stays_lossless()
    {
        // The frames' VP8L data sits inside ANMF chunks (gif2webp writes this by default).
        var input = _dir.Combine("anim.webp");
        using (var frames = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Teal })
            {
                var frame = new MagickImage(color, 48, 48) { AnimationDelay = 10 };
                frame.AddNoise(NoiseType.Random);
                frame.Settings.SetDefine(MagickFormat.WebP, "lossless", "true");
                frames.Add(frame);
            }

            frames.Write(input, MagickFormat.WebP);
        }

        var output = await Op().RunAsync(input, CancellationToken.None);

        using var before = new MagickImageCollection(input);
        using var after = new MagickImageCollection(output);
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
            Assert.Equal(0, before[i].Compare(after[i], ErrorMetric.Absolute));
    }

    [Fact]
    public async Task An_animated_gif_keeps_its_frames()
    {
        var input = _dir.Combine("anim.gif");
        using (var animation = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
                animation.Add(new MagickImage(color, 16, 16) { AnimationDelay = 10 });
            animation.Write(input, MagickFormat.Gif);
        }

        var output = await Op().RunAsync(input, CancellationToken.None);

        using var result = new MagickImageCollection(output);
        Assert.Equal(3, result.Count);
    }
}
