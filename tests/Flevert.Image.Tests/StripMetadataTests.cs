using Flevert.Core;
using ImageMagick;

namespace Flevert.Image.Tests;

public sealed class StripMetadataTests : IDisposable
{
    private static readonly string Heic = Path.Combine(AppContext.BaseDirectory, "fixtures", "gradient-320x240.heic");
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation StripOp() => ImageOperations.All.Single(op => op.Id == "image.strip-metadata");

    [Fact]
    public void Remove_metadata_is_offered_for_every_readable_image()
    {
        Assert.Equal("Remove metadata", StripOp().MenuPath);
        Assert.Equal(ImageFile.ReadableExtensions.Order(StringComparer.Ordinal), StripOp().Extensions.Order(StringComparer.Ordinal));
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

        var output = await StripOp().RunAsync(input, CancellationToken.None);

        Assert.Equal("IMG_2024 (no metadata).jpg", Path.GetFileName(output));
        using var result = new MagickImage(output);
        Assert.Null(result.GetExifProfile());
        Assert.Null(result.GetXmpProfile());
        Assert.Null(result.GetIptcProfile());
        Assert.Null(result.Comment);
        Assert.NotNull(result.GetColorProfile());
        Assert.Equal((20u, 40u), (result.Width, result.Height)); // rotated, since the orientation tag is gone
    }

    // The promise users act on before sharing a photo: no location, camera or embedded text is left, in any format that
    // can carry them. Checked on the profiles and on the raw bytes, so a copy hidden elsewhere in the file also fails.
    [Theory]
    [InlineData("photo.jpg", MagickFormat.Jpeg)]
    [InlineData("photo.png", MagickFormat.Png)]
    [InlineData("photo.webp", MagickFormat.WebP)]
    [InlineData("photo.avif", MagickFormat.Avif)]
    // TIFF is missing because ImageMagick cannot write EXIF into a TIFF to build the input (ADR-0004); HEIC, which it
    // cannot write at all, has its own fixture made by pillow-heif (portrait-gps.heic).
    public async Task Location_and_camera_details_are_removed_from_every_format_that_carries_them(string name, MagickFormat format)
    {
        var input = _dir.Combine(name);
        using (var image = new MagickImage(MagickColors.Gray, 64, 48))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "SecretCamMake");
            exif.SetValue(ExifTag.GPSLatitudeRef, "N");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(48, 1), new Rational(51, 1), new Rational(30, 1)]);
            exif.SetValue(ExifTag.GPSLongitudeRef, "E");
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(2, 1), new Rational(17, 1), new Rational(40, 1)]);
            image.SetProfile(exif);
            image.SetProfile(new XmpProfile(System.Text.Encoding.UTF8.GetBytes(
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
                + "<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>SecretXmpTitle</dc:title></rdf:Description>"
                + "</rdf:RDF></x:xmpmeta>")));
            image.Write(input, format);
        }

        using (var check = new MagickImage(input))
        {
            Assert.True(check.GetExifProfile()?.GetValue(ExifTag.GPSLatitude) is not null, $"control: {format} input carries GPS");
            Assert.True(check.GetXmpProfile() is not null, $"control: {format} input carries XMP");
        }
        Assert.Contains("SecretCamMake", Latin1(input), StringComparison.Ordinal);

        var output = await StripOp().RunAsync(input, CancellationToken.None);

        using (var result = new MagickImage(output))
        {
            Assert.Null(result.GetExifProfile());
            Assert.Null(result.GetXmpProfile());
        }

        var bytes = Latin1(output);
        Assert.DoesNotContain("SecretCamMake", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretXmpTitle", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("xmpmeta", bytes, StringComparison.Ordinal);
    }

    private static string Latin1(string path) => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path));

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

        var output = await StripOp().RunAsync(input, CancellationToken.None);

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

        var stripped = await StripOp().RunAsync(input, CancellationToken.None);
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

        var output = await StripOp().RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.StartsWith(expected, result.GetAttribute("jpeg:sampling-factor"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subsampling_follows_a_quarter_turn_of_the_pixels()
    {
        // 4:2:2 halves the chroma horizontally; after the pixels turn 90 degrees, it must halve it vertically instead.
        var input = _dir.Combine("portrait.jpg");
        using (var image = new MagickImage(MagickColors.SteelBlue, 200, 150))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Orientation, (ushort)6);
            image.SetProfile(exif);
            image.Orientation = Orientation.RightTop;
            image.Quality = 92;
            image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "2x1");
            image.Write(input, MagickFormat.Jpeg);
        }

        var output = await StripOp().RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.Equal((150u, 200u), (result.Width, result.Height));
        Assert.StartsWith("1x2", result.GetAttribute("jpeg:sampling-factor"), StringComparison.Ordinal);
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

        var output = await StripOp().RunAsync(input, CancellationToken.None);

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

        var output = await StripOp().RunAsync(input, CancellationToken.None);

        using var before = new MagickImageCollection(input);
        using var after = new MagickImageCollection(output);
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
            Assert.Equal(0, before[i].Compare(after[i], ErrorMetric.Absolute));
    }

    [Fact]
    public async Task A_blended_animated_webp_keeps_every_frame_intact()
    {
        // gif2webp-style frames cover the canvas and are alpha-blended: transparent means "keep the previous frame".
        // Written without expanding them first, most of each frame became transparent.
        var input = _dir.Combine("sticker.webp");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "blended-240x180.webp"), input);

        var output = await StripOp().RunAsync(input, CancellationToken.None);

        // ImageMagick ignores the blend flag when it reads, so comparing through it hides the damage (checked: a decoder
        // that honours it, Pillow, showed frames 1-5 broken). What breaks viewers is a frame stored "do not blend" while
        // it still carries the transparent "keep" pixels; the source animation is opaque.
        var frames = AnimationFrames(await File.ReadAllBytesAsync(output));
        Assert.Equal(6, frames.Count);
        Assert.DoesNotContain(frames, f => !f.Blend && f.HasAlpha);
    }

    /// <summary>Per ANMF chunk: whether the frame is alpha-blended onto the previous one, and whether it carries alpha.</summary>
    private static List<(bool Blend, bool HasAlpha)> AnimationFrames(byte[] webp)
    {
        var frames = new List<(bool, bool)>();
        for (var pos = 12; pos + 8 <= webp.Length;)
        {
            var size = BitConverter.ToInt32(webp, pos + 4);
            if (System.Text.Encoding.ASCII.GetString(webp, pos, 4) == "ANMF")
            {
                var body = pos + 8;
                var blend = (webp[body + 15] & 2) == 0;
                var sub = System.Text.Encoding.ASCII.GetString(webp, body + 16, 4);
                var hasAlpha = sub == "ALPH" || (sub == "VP8L" && ((BitConverter.ToUInt32(webp, body + 25) >> 28) & 1) == 1);
                frames.Add((blend, hasAlpha));
            }

            pos += 8 + size + (size & 1);
        }

        return frames;
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

        var output = await StripOp().RunAsync(input, CancellationToken.None);

        using var result = new MagickImageCollection(output);
        Assert.Equal(3, result.Count);
    }
}
