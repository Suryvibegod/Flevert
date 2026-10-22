using Flevert.Core;
using ImageMagick;

namespace Flevert.Image.Tests;

/// <summary>Behaviour shared by every operation: how files are read and written, and what survives.</summary>
[Collection(nameof(SequentialTimingTests))]
public sealed class ImageIoTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static IOperation Op(string id) => ImageOperations.All.Single(op => op.Id == id);

    // ImageMagick reads "%d", "%s" and similar in a file name as a pattern (scene number, base name), so a folder named
    // like that sent the temporary output elsewhere and left ".partial" files behind.
    [Theory]
    [InlineData("Scans %d", "image.to-png", "a.jpg")]
    [InlineData("Scans %d", "image.compress-balanced", "a.jpg")]
    [InlineData("Scans %d", "image.strip-metadata", "a.jpg")]
    [InlineData("p%s q", "image.to-webp", "anim.gif")]
    [InlineData("p%s q", "image.strip-metadata", "anim.gif")]
    [InlineData("p%03d", "image.compress-under-1mb", "a.jpg")]
    public async Task Folders_whose_names_look_like_imagemagick_patterns_work(string folder, string operationId, string source)
    {
        var dir = Directory.CreateDirectory(_dir.Combine(folder)).FullName;
        var input = Path.Combine(dir, source);
        using (var frames = new MagickImageCollection())
        using (var file = File.Create(input)) // through a stream: the fixture must not hit the bug under test
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Blue })
                frames.Add(new MagickImage(color, 16, 16) { AnimationDelay = 10 });
            if (source.EndsWith(".gif", StringComparison.Ordinal))
                frames.Write(file, MagickFormat.Gif);
            else
            {
                frames[0].Comment = new string('x', 20_000); // something for Compress to remove, so it is not skipped
                frames[0].Write(file, MagickFormat.Jpeg);
            }
        }

        // The runner passes the final path: \\?\ form on Windows, plain on Linux.
        var output = await Op(operationId).RunAsync((OperatingSystem.IsWindows() ? @"\\?\" : "") + input, CancellationToken.None);

        Assert.Equal(dir, Path.GetDirectoryName(output.Replace(@"\\?\", "", StringComparison.Ordinal)));
        Assert.True(File.Exists(output), output);
        Assert.Empty(Directory.GetFiles(dir, ".flevert-*"));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.partial", SearchOption.AllDirectories));
    }

    // The runner abandons an operation on Cancel; the batch ends soon after, killing a thread still encoding, and its
    // .partial file stays behind. Operations must stop promptly themselves.
    [Theory]
    [InlineData("image.to-webp")]
    [InlineData("image.compress-strong")]
    [InlineData("image.strip-metadata")]
    public async Task Cancel_stops_a_long_encode_promptly_and_leaves_no_partial_file(string operationId)
    {
        var input = _dir.Combine("big.png");
        using (var image = new MagickImage(MagickColors.Gray, 6000, 4000))
        {
            image.AddNoise(NoiseType.Random);
            image.Write(input, MagickFormat.Png);
        }

        // Decoding cannot be cancelled and its speed varies (0.2 s on a desktop, over 3 s on a busy CI runner), so the
        // bound scales with this machine's decode time, but never below 5 s: on CI a cancelled strip-metadata still took
        // 2.4 s while its measured decode was 0.36 s. Uncancelled, to-webp and compress-strong take 9 and 18 s on a
        // desktop, so they are still caught; strip-metadata (2 s uncancelled) is too quick for the bound to matter, and
        // only the exception and the missing partial file are checked for it.
        var decode = System.Diagnostics.Stopwatch.StartNew();
        using (new MagickImage(input))
        {
        }

        decode.Stop();
        var bound = TimeSpan.FromSeconds(Math.Max(5, 1 + 2 * decode.Elapsed.TotalSeconds));

        // Cancel once this machine has decoded the image, so the cancel usually lands in the encode the test is about
        // (the operation's own decode can still be slower). ImageMagick reports encode progress row by row for PNG, JPEG
        // and WebP (4000 events for this image, gaps under 2 s), so a cancelled encode stops quickly.
        using var cancel = new CancellationTokenSource(decode.Elapsed + TimeSpan.FromMilliseconds(500));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var cancelledAt = TimeSpan.Zero;
        using var _ = cancel.Token.Register(() => cancelledAt = timer.Elapsed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op(operationId).RunAsync(input, cancel.Token));

        // An encode that never checks the token would not throw at all; one that checks it only at the end takes
        // the whole encode after the cancel.
        var afterCancel = timer.Elapsed - cancelledAt;
        Assert.True(afterCancel < bound, $"stopped {afterCancel} after the cancel (bound {bound}, decode {decode.Elapsed})");
        Assert.Equal(["big.png"], _dir.FileNames());
    }

    // AVIF encoding reports no progress, so Cancel cannot stop it; if flevert.exe exits meanwhile, a temporary file opened
    // before encoding would stay behind. Nothing is created until there is something to write.
    [Fact]
    public async Task No_temporary_file_exists_while_an_image_is_still_being_encoded()
    {
        var input = _dir.Combine("big.png");
        using (var image = new MagickImage(MagickColors.Gray, 6000, 4000))
        {
            image.AddNoise(NoiseType.Gaussian, 0.4);
            image.Write(input, MagickFormat.Png);
        }

        var work = Op("image.to-avif").RunAsync(input, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1.5)); // decoding takes about half of that; then the long encode starts
        var duringEncode = Directory.GetFiles(_dir.Path, ".flevert-*");
        var stillEncoding = !work.IsCompleted;
        await work;

        Assert.True(stillEncoding, "control: the encode should still be running after 1.5 s");
        Assert.Empty(duringEncode);
    }

    // A CMYK JPG (from print workflows) carries a CMYK profile; converting it to RGB without that profile shifts colours
    // by tens of levels.
    [Theory]
    [InlineData("image.to-png")]
    [InlineData("image.to-webp")]
    [InlineData("image.compress-balanced")]
    public async Task Cmyk_images_are_converted_to_srgb_through_their_profile(string operationId)
    {
        var input = _dir.Combine("print.jpg");
        using (var image = new MagickImage(new MagickColor("#3a9be0"), 64, 64))
        {
            image.SetProfile(ColorProfiles.SRGB);
            image.TransformColorSpace(ColorProfiles.SRGB, ColorProfiles.USWebCoatedSWOP);
            image.Quality = 98;
            image.Write(input, MagickFormat.Jpeg);
        }

        IMagickColor<byte> managed, naive;
        using (var reference = new MagickImage(input))
        {
            Assert.Equal(ColorSpace.CMYK, reference.ColorSpace);
            using var naiveCopy = reference.Clone();
            naiveCopy.ColorSpace = ColorSpace.sRGB;
            naive = naiveCopy.GetPixels().GetPixel(32, 32).ToColor()!;
            reference.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
            managed = reference.GetPixels().GetPixel(32, 32).ToColor()!;
        }

        Assert.True(Distance(naive, managed) > 15, $"control: naive {naive} vs managed {managed}");

        var output = await Op(operationId).RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.Equal(ColorSpace.sRGB, result.ColorSpace);
        var actual = result.GetPixels().GetPixel(32, 32).ToColor()!;
        Assert.True(Distance(actual, managed) <= 8, $"got {actual}, colour-managed {managed}, naive {naive}");
    }

    private static int Distance(IMagickColor<byte> a, IMagickColor<byte> b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    // A misnamed file is decoded as what it is; the output must follow that too, not the name.
    [Fact]
    public async Task Removing_metadata_from_a_png_named_jpg_writes_a_png_and_keeps_transparency()
    {
        var input = _dir.Combine("logo.jpg");
        using (var image = new MagickImage(MagickColors.Transparent, 20, 20))
        {
            image.Draw(new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Red).Rectangle(10, 0, 19, 19));
            image.Write(input, MagickFormat.Png);
        }

        var output = await Op("image.strip-metadata").RunAsync(input, CancellationToken.None);

        Assert.Equal("logo (no metadata).png", Path.GetFileName(output));
        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, result.Format);
        Assert.True(result.HasAlpha);
    }

    [Fact]
    public async Task Compressing_a_jpg_named_png_compresses_it_as_a_jpg()
    {
        var input = _dir.Combine("photo.png");
        using (var image = new MagickImage(MagickColors.SteelBlue, 800, 600))
        {
            image.AddNoise(NoiseType.Gaussian, 0.4);
            image.Quality = 97;
            image.Write(input, MagickFormat.Jpeg);
        }

        var output = await Op("image.compress-balanced").RunAsync(input, CancellationToken.None);

        Assert.Equal("photo (compressed).jpg", Path.GetFileName(output));
        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.Jpeg, result.Format);
    }

    [Fact]
    public async Task An_animated_gif_named_jpg_stays_animated_when_converted_to_gif()
    {
        var input = _dir.Combine("reaction.jpg");
        using (var frames = new MagickImageCollection())
        {
            foreach (var color in new[] { MagickColors.Red, MagickColors.Green, MagickColors.Blue })
                frames.Add(new MagickImage(color, 16, 16) { AnimationDelay = 10 });
            frames.Write(input, MagickFormat.Gif);
        }

        var output = await Op("image.to-gif").RunAsync(input, CancellationToken.None);

        using var result = new MagickImageCollection(output);
        Assert.Equal(3, result.Count);
    }

    // Strip() also drops the PNG colour chunks; a Display P3 screenshot would then shift colour.
    [Theory]
    [InlineData("image.strip-metadata")]
    [InlineData("image.compress-light")]
    [InlineData("image.compress-strong")]
    public async Task A_png_keeps_its_colour_profile(string operationId)
    {
        var input = _dir.Combine("screenshot.png");
        using (var image = new MagickImage(MagickColors.Orange, 32, 32))
        {
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            image.SetAttribute("Author", "Jane Doe"); // a PNG tEXt chunk
            image.Comment = "secret";
            image.Write(input, MagickFormat.Png);
        }

        using (var check = new MagickImage(input))
        {
            Assert.NotNull(check.GetColorProfile());
            Assert.Equal("Jane Doe", check.GetAttribute("Author"));
        }

        var output = await Op(operationId).RunAsync(input, CancellationToken.None);

        using var result = new MagickImage(output);
        Assert.Equal(MagickFormat.Png, result.Format);
        Assert.Equal(ColorProfiles.AdobeRGB1998.Description, result.GetColorProfile()?.Description);
        Assert.Null(result.GetAttribute("Author"));
        Assert.Null(result.Comment);
    }
}
