using ImageMagick;

namespace Flevert.Pdf.Tests;

/// <summary>Image files of every type Flevert reads, for the images-to-PDF tests, written by ImageMagick at run time.</summary>
internal static class TestImages
{
    /// <summary>
    /// <see cref="TestJpeg.Picture"/> (red block top-left, blue block bottom-right, as stored) written to
    /// <paramref name="path"/> as <paramref name="format"/>, with <paramref name="density"/> when given.
    /// </summary>
    public static string Picture(string path, uint width, uint height, MagickFormat format, Density? density = null, Action<MagickImage>? configure = null)
    {
        using var image = TestJpeg.Picture(width, height);
        if (density is not null)
            image.Density = density;
        configure?.Invoke(image);
        File.WriteAllBytes(path, image.ToByteArray(format));
        return path;
    }

    /// <summary>A PNG whose left half is opaque green and right half fully transparent.</summary>
    public static string TransparentPng(string path, uint width, uint height, Density? density = null)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (int)(((y * width) + x) * 4);
                (rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]) = x < width / 2 ? ((byte)0, (byte)255, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)0, (byte)0);
            }
        }

        using var image = new MagickImage();
        image.ReadPixels(rgba, new PixelReadSettings(width, height, StorageType.Char, PixelMapping.RGBA));
        if (density is not null)
            image.Density = density;
        File.WriteAllBytes(path, image.ToByteArray(MagickFormat.Png));
        return path;
    }

    /// <summary>A greyscale PNG (colour type 0): left half black, right half white.</summary>
    public static string GreyPng(string path, uint width, uint height)
    {
        var grey = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                grey[(y * width) + x] = x < width / 2 ? (byte)0 : (byte)255;
        }

        using var image = new MagickImage();
        image.ReadPixels(grey, new PixelReadSettings(width, height, StorageType.Char, "R"));
        image.ColorType = ColorType.Grayscale;
        var bytes = image.ToByteArray(MagickFormat.Png);
        if (bytes[25] != 0)
            throw new InvalidOperationException($"ImageMagick wrote PNG colour type {bytes[25]}, not greyscale.");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A two-frame GIF animation: the first frame solid blue, the second solid red.</summary>
    public static string BlueThenRedGif(string path, uint width, uint height)
    {
        using var frames = new MagickImageCollection
        {
            new MagickImage(MagickColors.Blue, width, height) { AnimationDelay = 50 },
            new MagickImage(MagickColors.Red, width, height) { AnimationDelay = 50 },
        };
        File.WriteAllBytes(path, frames.ToByteArray(MagickFormat.Gif));
        return path;
    }
}
