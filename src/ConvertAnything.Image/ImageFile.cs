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
    /// <param name="allFrames">False decodes only the first frame or page: a 1000-frame GIF converted to PNG need not
    /// decode the other 999.</param>
    public static MagickImageCollection Read(string path, bool allFrames = true)
    {
        MagickSetup.EnsureInitialized();
        if (!Formats.TryGetValue(Path.GetExtension(path), out var format))
            throw new NotSupportedException($"{Path.GetExtension(path)} is not a supported image type.");

        var settings = new MagickReadSettings { Format = format };
        if (!allFrames)
        {
            settings.FrameIndex = 0;
            settings.FrameCount = 1;
        }

        // Photoshop layers embedded in a TIFF are parsed by a separate, larger decoder; the composite image is enough.
        settings.SetDefine(MagickFormat.Tiff, "ignore-layers", "true");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new MagickImageCollection(stream, settings);
    }

    /// <summary>
    /// Makes <paramref name="images"/> stop as soon as <paramref name="ct"/> is cancelled: ImageMagick checks its progress
    /// callback while it processes and encodes. The runner abandons a cancelled operation and the process may exit soon
    /// after, so a long encode must not keep running (and leave its temporary file behind).
    /// </summary>
    public static void StopOnCancel(IEnumerable<IMagickImage<byte>> images, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var image in images)
            image.Progress += (_, e) => e.Cancel |= ct.IsCancellationRequested;
    }

    /// <summary>Runs image work on the thread pool, turning ImageMagick's "cancelled" error into a cancellation.</summary>
    public static Task RunAsync(Action work, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            work();
        }
        catch (MagickException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }, ct);

    /// <summary>Formats whose frames are an animation (as opposed to TIFF, whose frames are pages).</summary>
    public static bool IsAnimationFormat(MagickFormat format) => format is MagickFormat.Gif or MagickFormat.WebP;

    /// <summary>
    /// Coalescing expands every frame to the full canvas; 1000 frames of a 400 MP canvas would be written to the pixel
    /// cache on disk before any limit stops it. Refuse animations whose expanded size exceeds the per-image area limit.
    /// </summary>
    public static void Coalesce(MagickImageCollection images)
    {
        var canvas = images[0].Page is { Width: > 0, Height: > 0 } page ? (ulong)page.Width * page.Height : (ulong)images[0].Width * images[0].Height;
        if (canvas * (ulong)images.Count > MagickSetup.MaxArea)
            throw new InvalidOperationException("The animation is too large to process.");
        images.Coalesce();
    }

    /// <summary>Whether a WebP file stores its pixels losslessly (a VP8L chunk), which ImageMagick does not report.</summary>
    public static bool IsLosslessWebP(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8))
            return false;

        Span<byte> chunk = stackalloc byte[8];
        while (stream.Read(chunk) == 8)
        {
            if (chunk[..4].SequenceEqual("VP8L"u8))
                return true;
            if (chunk[..4].SequenceEqual("VP8 "u8))
                return false;
            var size = BitConverter.ToUInt32(chunk[4..]);
            stream.Seek(size + (size & 1), SeekOrigin.Current); // chunks are padded to an even size
        }

        return false;
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
        // Multi-page TIFF cannot be written to a .NET stream (libtiff fails linking directories); an in-memory blob works.
        var bytes = images.ToByteArray(format);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
}
