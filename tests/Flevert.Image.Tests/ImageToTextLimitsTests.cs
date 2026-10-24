using System.Drawing;
using System.Drawing.Imaging;
using Flevert.Core;
using Flevert.Ocr;
using ImageMagick;

namespace Flevert.Image.Tests;

/// <summary>
/// What Text from pictures hands tesseract (a size it can read in time and memory) and how it treats the page PNG it
/// reads back (the module's safe read, and OSD that cannot answer).
/// </summary>
public sealed class ImageToTextLimitsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Tall_screenshot_is_read_without_going_over_the_image_limits()
    {
        var input = _dir.Combine("chat.png");
        WriteBlank(input, 720, 25_000);
        var tesseract = new FakeTesseract();

        var output = await new ImageToText(tesseract.Engine).RunAsync(input, CancellationToken.None);

        Assert.Equal("hello\r\n", await File.ReadAllTextAsync(output));
        var (width, height) = Assert.Single(tesseract.ReadSizes);
        Assert.InRange(height, 25_000u, 32_767u); // tesseract refuses a side over 32767 px; ImageMagick one over 40000
        Assert.InRange(width, 720u, 1440u); // never made smaller than it was: the text is small
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Large_photo_is_scaled_down_before_tesseract()
    {
        var input = _dir.Combine("phone.png");
        WriteBlank(input, 8000, 6000); // 48 MP
        var tesseract = new FakeTesseract();

        await new ImageToText(tesseract.Engine).RunAsync(input, CancellationToken.None);

        var (width, height) = Assert.Single(tesseract.ReadSizes);
        Assert.InRange(width, 5000u, 6000u);
        Assert.InRange((double)width / height, 8000.0 / 6000 * 0.99, 8000.0 / 6000 * 1.01);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Small_screenshot_is_still_doubled()
    {
        var input = _dir.Combine("small.png");
        WriteBlank(input, 600, 150);
        var tesseract = new FakeTesseract();

        await new ImageToText(tesseract.Engine).RunAsync(input, CancellationToken.None);

        Assert.Equal((1200u, 300u), Assert.Single(tesseract.ReadSizes));
    }

    [Theory]
    [InlineData(600u, 150u, 1200u, 300u)] // small text: doubled
    [InlineData(2480u, 3508u, 2480u, 3508u)] // an A4 scan at 300 DPI: as it is
    [InlineData(4000u, 500u, 8000u, 1000u)] // a wide strip: doubled, still small
    [InlineData(8000u, 6000u, 5773u, 4330u)] // a 48 MP photo: 25 MP
    [InlineData(20_000u, 10_000u, 7071u, 3535u)] // a 200 MP panorama: 25 MP
    [InlineData(720u, 25_000u, 848u, 29_462u)] // a long screenshot: as large as 25 MP allows
    [InlineData(500u, 39_000u, 384u, 30_000u)] // a longer one: no side over 30000 px
    public void Pictures_are_scaled_to_what_tesseract_reads_well(uint width, uint height, uint expectedWidth, uint expectedHeight)
    {
        var (w, h) = ImageToText.OcrSize(width, height);

        Assert.Equal((expectedWidth, expectedHeight), (w, h));
        Assert.True((ulong)w * h <= ImageToText.MaxOcrPixels || (w, h) == (width, height));
        Assert.True(Math.Max(w, h) <= ImageToText.MaxOcrSide);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Tall_screenshot_text_is_read_by_tesseract()
    {
        var input = _dir.Combine("long chat.png");
        using (var bmp = new Bitmap(720, 25_000))
        {
            bmp.SetResolution(96, 96);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                using var font = new Font("Arial", 14, FontStyle.Regular, GraphicsUnit.Point);
                g.DrawString("Hello from the top of a long chat", font, Brushes.Black, 20, 40);
                g.DrawString("Goodbye at the bottom of it", font, Brushes.Black, 20, 24_900);
            }

            bmp.Save(input, ImageFormat.Png);
        }

        var output = await ImageOperations.All.Single(op => op.Id == "image.to-text").RunAsync(input, CancellationToken.None);

        var text = await File.ReadAllTextAsync(output);
        Assert.Contains("top of a long chat", text, StringComparison.Ordinal);
        Assert.Contains("bottom of it", text, StringComparison.Ordinal);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Turning_upright_refuses_a_png_that_holds_another_format()
    {
        // The PDF worker writes the page PNG; flevert.exe must read it back only as PNG, under the module's policy.
        var page = _dir.Combine("page.png");
        using (var bmp = new MagickImage(MagickColors.White, 40, 20))
            await File.WriteAllBytesAsync(page, bmp.ToByteArray(MagickFormat.Bmp));
        var tesseract = new FakeTesseract { Osd = () => Ok("Rotate: 90\nOrientation confidence: 30\n") };

        await Assert.ThrowsAnyAsync<MagickException>(() => ImageToText.ReadTextUprightAsync(tesseract.Engine, page, CancellationToken.None));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Turning_upright_turns_a_png_page()
    {
        var page = _dir.Combine("page.png");
        WriteBlank(page, 40, 20);
        var tesseract = new FakeTesseract { Osd = () => Ok("Rotate: 90\nOrientation confidence: 30\n") };

        var turned = (await ImageToText.ReadTextUprightAsync(tesseract.Engine, page, CancellationToken.None)).Turned;

        Assert.Equal(90, turned);
        using var info = ImageFile.Ping(page);
        Assert.Equal((20u, 40u), (info[0].Width, info[0].Height));
        Assert.Equal(300, Math.Round(info[0].Density.ChangeUnits(DensityUnit.PixelsPerInch).X));
    }

    public static TheoryData<string> OsdFailures => ["crash", "timeout", "memory"];

    [WindowsTheory]
    [SupportedOSPlatform("windows")]
    [MemberData(nameof(OsdFailures))]
    public async Task A_failed_orientation_check_reads_the_page_as_it_is(string failure)
    {
        var page = _dir.Combine("page.png");
        WriteBlank(page, 40, 20);
        var tesseract = new FakeTesseract
        {
            Osd = failure switch
            {
                "crash" => () => new ExternalToolResult(-1073741819, "", "access violation", false, false),
                "timeout" => () => throw new TimeoutException("OSD took too long"),
                _ => () => new ExternalToolResult(1, "", "", false, false) { MemoryLimitExceeded = true },
            },
        };

        Assert.Equal(0, (await ImageToText.ReadTextUprightAsync(tesseract.Engine, page, CancellationToken.None)).Turned);
        Assert.Equal("hello", await ImageToText.ReadUprightAsync(tesseract.Engine, page, CancellationToken.None));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Cancelling_during_the_orientation_check_still_cancels()
    {
        var page = _dir.Combine("page.png");
        WriteBlank(page, 40, 20);
        using var cts = new CancellationTokenSource();
        var tesseract = new FakeTesseract
        {
            Osd = () =>
            {
                cts.Cancel();
                throw new OcrException(OcrException.ToolFailedMessage);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageToText.ReadTextUprightAsync(tesseract.Engine, page, cts.Token));
    }

    private static void WriteBlank(string path, uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.White, width, height);
        image.Write(path, MagickFormat.Png);
    }

    private static ExternalToolResult Ok(string output) => new(0, output, "", false, false);

    /// <summary>Answers like tesseract: OSD as set, text "hello", noting the size of each picture it is asked to read.</summary>
    private sealed class FakeTesseract
    {
        public Func<ExternalToolResult> Osd { get; init; } = () => Ok("Rotate: 0\nOrientation confidence: 20\n");

        public List<(uint Width, uint Height)> ReadSizes { get; } = [];

        public Tesseract Engine => new(Tesseract.NextToApp.ExecutablePath, Tesseract.NextToApp.TessDataPath, Run);

        private Task<ExternalToolResult> Run(
            string path, IReadOnlyList<string> args, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            if (args.Contains("osd"))
                return Task.FromResult(Osd());

            using (var info = ImageFile.Ping(args[0]))
            {
                lock (ReadSizes)
                    ReadSizes.Add((info[0].Width, info[0].Height));
            }

            return Task.FromResult(Ok("hello"));
        }
    }
}
