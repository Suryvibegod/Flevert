using System.Globalization;
using System.IO.Compression;
using System.Text;
using ImageMagick;
using ImageMagick.Drawing;
using Flevert.Image;

namespace Flevert.Pdf.Tests;

/// <summary>
/// PDFs of images for Compress for sharing (docs/specs/pdf.md, P1), written at run time: scanned pages as JPEGs at
/// 300 DPI, an image drawn at two sizes, one with a soft mask, one stored as Flate, one in CMYK, a 1-bit one. The pages
/// are drawn by ImageMagick (dark word-sized blocks on paper, a photograph-like block on some), never taken from a
/// person's files.
/// </summary>
internal static class SharingPdf
{
    public const double A4Width = 595.28;
    public const double A4Height = 841.89;

    /// <summary>A4 drawn whole: the matrix of an image filling the page.</summary>
    public static readonly string FullPage = Matrix(A4Width, A4Height, 0, 0);

    /// <summary>An image XObject: its dictionary without /Length (<c>{2}</c> stands for the object number of image 2), and its stored data.</summary>
    public sealed record Image(string Dictionary, byte[] Data);

    public static string Matrix(double width, double height, double x, double y) =>
        string.Create(CultureInfo.InvariantCulture, $"{width} 0 0 {height} {x} {y}");

    /// <summary>A page of A4 at 300 DPI, as a scanner writes it: 2480 x 3508 px, JPEG of quality 85.</summary>
    /// <param name="noise">With a scanner's sensor noise (<see cref="ScanPage"/>); without, a page as clean as a scan gets.</param>
    public static byte[] ScanJpeg(int seed, uint quality = 85, bool noise = true)
    {
        using var page = ScanPage(seed, noise: noise);
        page.Quality = quality;
        return page.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>
    /// A page of word-sized dark blocks on paper, a photograph-like block on every third, slightly blurred as a scanner's
    /// optics do; with <paramref name="noise"/>, a sensor's noise too: brightness a few levels up or down, the same in red,
    /// green and blue (a sensor's noise is mostly in brightness, and JPEG keeps colour at half resolution anyway), in
    /// grains of about two pixels rather than independent from pixel to pixel.
    /// </summary>
    public static MagickImage ScanPage(int seed, uint width = 2480, uint height = 3508, bool noise = true)
    {
        MagickSetup.EnsureInitialized();
        var random = new Random(seed);
        var image = new MagickImage(new MagickColor(250, 248, 242), width, height);
        var drawables = new Drawables().FillColor(new MagickColor(25, 25, 35));
        for (var y = 200; y < height - 250; y += 60)
        {
            for (var x = 200; x < width - 300;)
            {
                var word = random.Next(40, 220);
                drawables.Rectangle(x, y, x + word, y + 30);
                x += word + 25;
            }
        }

        drawables.Draw(image);
        if (seed % 3 == 0)
        {
            using var photo = Gradient(900, 600, (0x40, 0x60, 0xA0), (0xE0, 0xC0, 0x80));
            image.Composite(photo, 300, 1300, CompositeOperator.Over);
        }

        image.GaussianBlur(0, 1);
        image.Strip();
        if (!noise)
            return image;

        // One brightness offset per two-by-two pixels, smoothed over the page by the resize.
        var (grainWidth, grainHeight) = ((width + 1) / 2, (height + 1) / 2);
        var grain = new byte[grainWidth * grainHeight];
        for (var i = 0; i < grain.Length; i++)
            grain[i] = (byte)(128 + random.Next(-NoiseLevels, NoiseLevels + 1));
        using var field = new MagickImage();
        field.ReadPixels(grain, new PixelReadSettings(grainWidth, grainHeight, StorageType.Char, "I"));
        field.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
        var offsets = field.GetPixels().ToByteArray("R")!;
        var pixels = image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)Math.Clamp(pixels[i] + offsets[i / 3] - 128, 0, 255);
        image.Dispose();
        var noisy = new MagickImage();
        noisy.ReadPixels(pixels, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGB));
        return noisy;
    }

    // How many levels of 255 the sensor noise goes up or down at most, before smoothing.
    private const int NoiseLevels = 6;

    /// <summary>
    /// A top-to-bottom gradient between two colours, from pixels: Flevert's ImageMagick policy allows no coder that makes
    /// images (<c>gradient:</c>, <c>xc:</c>), as it allows nothing the converted files do not need.
    /// </summary>
    public static MagickImage Gradient(uint width, uint height, (byte R, byte G, byte B) from, (byte R, byte G, byte B) to)
    {
        MagickSetup.EnsureInitialized();
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var t = height == 1 ? 0 : (double)y / (height - 1);
            var colour = new[] { from.R + ((to.R - from.R) * t), from.G + ((to.G - from.G) * t), from.B + ((to.B - from.B) * t) };
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                    rgb[(((y * width) + x) * 3) + c] = (byte)Math.Round(colour[c]);
            }
        }

        var image = new MagickImage();
        image.ReadPixels(rgb, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGB));
        return image;
    }

    /// <summary>The pixels of <paramref name="image"/> as 8-bit RGB, or gray, compressed with Flate.</summary>
    public static byte[] Flate(MagickImage image, bool gray = false)
    {
        var pixels = gray ? image.GetPixels().ToByteArray(PixelMapping.RGB)!.Where((_, i) => i % 3 == 0).ToArray() : image.GetPixels().ToByteArray(PixelMapping.RGB)!;
        var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(pixels);
        return packed.ToArray();
    }

    public static Image Jpeg(byte[] jpeg, uint width, uint height, string colorspace = "/DeviceRGB", string extra = "") =>
        new(string.Create(CultureInfo.InvariantCulture,
            $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorspace} /BitsPerComponent 8 /Filter /DCTDecode {extra}"), jpeg);

    /// <summary>
    /// Writes A4 pages, each drawing the images it lists (an index into <paramref name="images"/>, and the matrix that
    /// places it).
    /// </summary>
    public static void Write(string path, IReadOnlyList<Image> images, params (int Image, string Matrix)[][] pages)
    {
        var objects = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', Enumerable.Range(0, pages.Length).Select(p => $"{3 + images.Count + (2 * p) + 1} 0 R"))}] >>")),
        };
        foreach (var image in images)
        {
            var dictionary = image.Dictionary;
            for (var i = 0; i < images.Count; i++)
                dictionary = dictionary.Replace($"{{{i}}}", $"{3 + i} 0 R", StringComparison.Ordinal);
            objects.Add([.. Ascii($"<< {dictionary} /Length {image.Data.Length} >>\nstream\n"), .. image.Data, .. Ascii("\nendstream")]);
        }

        for (var p = 0; p < pages.Length; p++)
        {
            var content = string.Concat(pages[p].Select(draw => $"q {draw.Matrix} cm /Im{draw.Image} Do Q\n"));
            var resources = string.Join(' ', pages[p].Select(draw => draw.Image).Distinct().Select(i => $"/Im{i} {3 + i} 0 R"));
            objects.Add([.. Ascii($"<< /Length {content.Length} >>\nstream\n{content}\nendstream")]);
            objects.Add(Ascii(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {A4Width} {A4Height}] /Resources << /XObject << {resources} >> >> /Contents {3 + images.Count + (2 * p)} 0 R >>")));
        }

        WriteObjects(path, objects);
    }

    /// <summary>A stream object: its dictionary without /Length, and its data.</summary>
    public static byte[] Stream(string dictionary, byte[] data) =>
        [.. Ascii($"<< {dictionary} /Length {data.Length} >>\nstream\n"), .. data, .. Ascii("\nendstream")];

    /// <summary>A stream object whose data is text: a content stream.</summary>
    public static byte[] Stream(string dictionary, string text) => Stream(dictionary, Ascii(text));

    /// <summary>A PDF of these objects, numbered from 1, whose catalog is object 1.</summary>
    public static void WriteObjects(string path, IReadOnlyList<byte[]> objects)
    {
        using var file = File.Create(path);
        var offsets = new List<long>();
        file.Write(Ascii("%PDF-1.7\n"));
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(file.Position);
            file.Write(Ascii($"{i + 1} 0 obj\n"));
            file.Write(objects[i]);
            file.Write(Ascii("\nendobj\n"));
        }

        var xref = file.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        file.Write(Ascii(table.ToString()));
    }

    /// <summary>The mean difference, per channel and out of 255, between page <paramref name="page"/> of two PDFs drawn at 150 DPI.</summary>
    public static double MeanDifference(string a, string b, int page)
    {
        using var first = PdfDocument.Open(a);
        using var second = PdfDocument.Open(b);
        var x = first.Render(page, 150);
        var y = second.Render(page, 150);
        Assert.Equal((x.Width, x.Height), (y.Width, y.Height));
        var left = x.Bgra.Span;
        var right = y.Bgra.Span;
        long sum = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (i % 4 != 3)
                sum += Math.Abs(left[i] - right[i]);
        }

        return sum / (left.Length * 0.75);
    }

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
