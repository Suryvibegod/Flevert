using ImageMagick;

namespace ConvertAnything.Image;

public static class ImageFile
{
    private static readonly Dictionary<string, MagickFormat> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MagickFormat.Jpeg,
        [".jpeg"] = MagickFormat.Jpeg,
        [".png"] = MagickFormat.Png,
        [".webp"] = MagickFormat.WebP,
        [".heic"] = MagickFormat.Heic,
        [".heif"] = MagickFormat.Heif,
        [".avif"] = MagickFormat.Avif,
        [".gif"] = MagickFormat.Gif,
        [".bmp"] = MagickFormat.Bmp,
        [".tif"] = MagickFormat.Tiff,
        [".tiff"] = MagickFormat.Tiff,
    };

    /// <summary>Extensions this module can read.</summary>
    public static IReadOnlyCollection<string> ReadableExtensions => Formats.Keys;

    /// <summary>The format a file with <paramref name="extension"/> is read as.</summary>
    public static MagickFormat FormatOf(string extension) =>
        Formats.TryGetValue(extension, out var format) ? format : throw new NotSupportedException($"{extension} is not a supported image type.");

    /// <summary>
    /// Reads all frames. The coder is chosen from the extension, never sniffed from the content, so a ".jpg" that really
    /// holds a script or another format fails instead of being interpreted. The file is opened here and handed to
    /// ImageMagick as a stream: given a path, it would read "%d", "[0]" and similar in it as patterns.
    /// </summary>
    public static MagickImageCollection Read(string path)
    {
        MagickSetup.EnsureInitialized();
        if (!Formats.TryGetValue(Path.GetExtension(path), out var format))
            throw new NotSupportedException($"{Path.GetExtension(path)} is not a supported image type.");

        var settings = new MagickReadSettings { Format = format };
        // Photoshop layers embedded in a TIFF are parsed by a separate, larger decoder; the composite image is enough.
        settings.SetDefine(MagickFormat.Tiff, "ignore-layers", "true");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new MagickImageCollection(stream, settings);
    }

    /// <summary>Writes one image to a new file through a stream, for the same reason as <see cref="Read"/>.</summary>
    public static void Write(IMagickImage<byte> image, string path, MagickFormat format)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        image.Write(stream, format);
    }

    /// <summary>Writes every frame (an animation or pages) to a new file through a stream.</summary>
    public static void Write(MagickImageCollection images, string path, MagickFormat format)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        images.Write(stream, format);
    }
}
