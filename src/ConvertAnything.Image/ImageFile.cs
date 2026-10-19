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
    /// Reads all frames. ImageMagick is never asked to guess the format: the decoder is chosen by our own signature check,
    /// within the allow-list, or else by the extension, so a ".jpg" that really holds a script fails instead of being
    /// interpreted. The file is opened here and handed to ImageMagick as a stream: given a path, it would read "%d", "[0]"
    /// and similar in it as patterns.
    /// </summary>
    /// <param name="allFrames">False decodes only the first frame or page: a 1000-frame GIF converted to PNG need not
    /// decode the other 999.</param>
    public static MagickImageCollection Read(string path, bool allFrames = true)
    {
        MagickSetup.EnsureInitialized();
        if (!Formats.TryGetValue(Path.GetExtension(path), out var format))
            throw new NotSupportedException($"{Path.GetExtension(path)} is not a supported image type.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Downloads are often misnamed (a WebP or PNG saved as ".jpg"). Our own signature check may pick another decoder,
        // but only from the allow-list; ImageMagick itself is never asked to guess.
        if (Signature(stream) is { } actual && Family(actual) != Family(format))
            format = actual;
        stream.Position = 0;

        var settings = new MagickReadSettings { Format = format };
        if (!allFrames)
        {
            settings.FrameIndex = 0;
            settings.FrameCount = 1;
        }

        // Photoshop layers embedded in a TIFF are parsed by a separate, larger decoder; the composite image is enough.
        settings.SetDefine(MagickFormat.Tiff, "ignore-layers", "true");
        return new MagickImageCollection(stream, settings);
    }

    /// <summary>The allowed format whose signature starts the file, or <c>null</c> if none matches.</summary>
    private static MagickFormat? Signature(Stream stream)
    {
        Span<byte> h = stackalloc byte[12];
        if (stream.ReadAtLeast(h, 12, throwOnEndOfStream: false) < 12)
            return null;
        return h switch
        {
            [0xFF, 0xD8, 0xFF, ..] => MagickFormat.Jpeg,
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, ..] => MagickFormat.Png,
            [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7' or (byte)'9', (byte)'a', ..] => MagickFormat.Gif,
            [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P'] => MagickFormat.WebP,
            [(byte)'B', (byte)'M', ..] => MagickFormat.Bmp,
            [(byte)'I', (byte)'I', 42, 0, ..] or [(byte)'M', (byte)'M', 0, 42, ..] => MagickFormat.Tiff,
            [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' or (byte)'s'] => MagickFormat.Avif,
            [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', ..] => MagickFormat.Heic, // heic, heix, mif1, ...
            _ => null,
        };
    }

    // HEIC, HEIF and AVIF share a container: a generic brand (mif1) cannot tell them apart, so trust the extension there.
    private static int Family(MagickFormat format) => format switch
    {
        MagickFormat.Heic or MagickFormat.Heif or MagickFormat.Avif => 1,
        _ => (int)format,
    };

    /// <summary>
    /// Makes <paramref name="images"/> stop as soon as <paramref name="ct"/> is cancelled: ImageMagick checks its progress
    /// callback while it processes and encodes. The runner abandons a cancelled operation and the process may exit soon
    /// after, so a long encode must not keep running (and leave its temporary file behind).
    /// </summary>
    /// <remarks>
    /// Must be called again after anything that replaces the frames (Coalesce, Optimize): ImageMagick copies the native
    /// callback pointer into the new frames, and once the replaced .NET images are collected the next callback calls a
    /// freed delegate, which terminates the process.
    /// </remarks>
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
    public static void Coalesce(MagickImageCollection images, CancellationToken ct)
    {
        var canvas = images[0].Page is { Width: > 0, Height: > 0 } page ? (ulong)page.Width * page.Height : (ulong)images[0].Width * images[0].Height;
        if (canvas * (ulong)images.Count > MagickSetup.MaxArea)
            throw new InvalidOperationException("The animation is too large to process.");
        images.Coalesce();
        StopOnCancel(images, ct); // the new frames still point at the replaced frames' callbacks; see StopOnCancel
    }

    /// <summary>Whether a WebP file stores its pixels losslessly (a VP8L chunk), which ImageMagick does not report.</summary>
    public static bool IsLosslessWebP(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[12];
        if (stream.ReadAtLeast(header, 12, throwOnEndOfStream: false) != 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WEBP"u8))
            return false;

        Span<byte> chunk = stackalloc byte[8];
        while (stream.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
        {
            if (chunk[..4].SequenceEqual("VP8L"u8))
                return true;
            if (chunk[..4].SequenceEqual("VP8 "u8))
                return false;
            if (chunk[..4].SequenceEqual("ANMF"u8))
            {
                // An animation frame: 16 bytes of position and timing, then the frame's own chunks (ALPH, VP8 or VP8L).
                stream.Seek(16, SeekOrigin.Current);
                continue;
            }

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
        // The pages are encoded in memory below, outside ImageMagick's limits; each page is within the area limit, but
        // many of them need not be.
        var area = images.Aggregate(0UL, (sum, image) => sum + ((ulong)image.Width * image.Height));
        if (area > MagickSetup.MaxArea)
            throw new InvalidOperationException("The image has too many large pages to process.");

        // Multi-page TIFF cannot be written to a .NET stream (libtiff fails linking directories); an in-memory blob works.
        var bytes = images.ToByteArray(format);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
}
