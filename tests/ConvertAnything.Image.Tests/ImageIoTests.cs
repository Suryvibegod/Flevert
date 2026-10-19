using ConvertAnything.Core;
using ImageMagick;

namespace ConvertAnything.Image.Tests;

/// <summary>Behaviour shared by every operation: how files are read and written, and what survives.</summary>
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
                frames[0].Write(file, MagickFormat.Jpeg);
        }

        var output = await Op(operationId).RunAsync(@"\\?\" + input, CancellationToken.None);

        Assert.Equal(dir, Path.GetDirectoryName(output.Replace(@"\\?\", "", StringComparison.Ordinal)));
        Assert.True(File.Exists(output), output);
        Assert.Empty(Directory.GetFiles(dir, ".ca-*"));
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

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Op(operationId).RunAsync(input, cancel.Token));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
        Assert.Equal(["big.png"], _dir.FileNames());
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
