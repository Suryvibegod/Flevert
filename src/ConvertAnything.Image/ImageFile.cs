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
    /// <param name="maxFrames">Decode at most this many frames (to tell whether there is more than one).</param>
    /// <param name="frameIndex">Start at this frame (with <paramref name="maxFrames"/> = 1 to read exactly one).</param>
    public static MagickImageCollection Read(string path, bool allFrames = true, uint? maxFrames = null, uint frameIndex = 0)
    {
        MagickSetup.EnsureInitialized();
        if (!Formats.TryGetValue(Path.GetExtension(path), out var format))
            throw new NotSupportedException($"{Path.GetExtension(path)} is not a supported image type.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Downloads are often misnamed (a WebP or PNG saved as ".jpg"). Our own signature check may pick another decoder,
        // but only from the allow-list; ImageMagick itself is never asked to guess.
        format = Choose(format, Signature(stream));
        stream.Position = 0;

        var settings = new MagickReadSettings { Format = format };
        if (!allFrames || maxFrames is not null || frameIndex > 0)
        {
            settings.FrameIndex = frameIndex;
            settings.FrameCount = allFrames ? maxFrames : 1;
        }

        // Photoshop layers embedded in a TIFF are parsed by a separate, larger decoder; the composite image is enough.
        settings.SetDefine(MagickFormat.Tiff, "ignore-layers", "true");
        return new MagickImageCollection(stream, settings);
    }

    /// <summary>
    /// Bytes of pixels every frame takes once decoded, from the headers only (ImageMagick's ping), opened and chosen the
    /// same safe way as <see cref="Read"/>. A frame counts at its canvas size, which an optimized animation's small
    /// frames reach once <see cref="Coalesce"/> expands them, at four bytes per pixel, or one channel more than ping
    /// reports when that is more. Ping reports no channels for most RGB formats and misses alpha: a CMYK + alpha TIFF
    /// pings as 4 channels and decodes as 5. Extra TIFF samples beyond that are not seen (accepted, SPEC-image).
    /// </summary>
    public static ulong DecodedBytes(string path)
    {
        MagickSetup.EnsureInitialized();
        var format = FormatOf(Path.GetExtension(path));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        format = Choose(format, Signature(stream));
        stream.Position = 0;

        var settings = new MagickReadSettings { Format = format };
        settings.SetDefine(MagickFormat.Tiff, "ignore-layers", "true");
        using var frames = new MagickImageCollection();
        frames.Ping(stream, settings);
        ulong bytes = 0;
        foreach (var frame in frames)
        {
            var canvas = frame.Page is { Width: > 0, Height: > 0 } page ? (ulong)page.Width * page.Height : 0;
            var pixels = Math.Max((ulong)frame.Width * frame.Height, canvas);
            bytes = checked(bytes + (pixels * Math.Max(4u, frame.ChannelCount + 1)));
        }

        return bytes;
    }

    /// <summary>
    /// The one real page of a TIFF, read an image at a time so that thumbnails stored before, between or after pages are
    /// skipped and at most two pages are ever decoded. A second real page means the request (one output image) cannot
    /// be fulfilled without dropping it, which fails.
    /// </summary>
    public static MagickImageCollection ReadFirstPage(string path)
    {
        MagickImageCollection? page = null;
        for (uint index = 0; ; index++)
        {
            MagickImageCollection image;
            try
            {
                image = Read(path, maxFrames: 1, frameIndex: index);
            }
            catch (MagickException) when (page is not null)
            {
                break; // past the last image
            }

            if (image.Count == 0)
            {
                image.Dispose();
                break;
            }

            if (IsThumbnail(image[0]))
            {
                image.Dispose();
                continue;
            }

            if (page is null)
            {
                page = image;
                continue;
            }

            image.Dispose();
            page.Dispose();
            throw new InvalidOperationException("The TIFF has several pages and the result is a single image; use Remove metadata to keep every page.");
        }

        return page ?? throw new InvalidOperationException("The TIFF holds only thumbnails.");
    }

    /// <summary>A reduced-resolution thumbnail stored as an extra TIFF image (NewSubfileType = 1), not a page.</summary>
    public static bool IsThumbnail(IMagickImage<byte> image) => image.GetAttribute("tiff:subfiletype") == "REDUCEDIMAGE";

    /// <summary>
    /// The format the file is decoded as: the allowed format its signature shows, or else the one its extension names.
    /// Operations choose their output from this, so a PNG saved as ".jpg" stays a PNG (with its transparency).
    /// </summary>
    public static MagickFormat RealFormat(string path)
    {
        var named = FormatOf(Path.GetExtension(path));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Choose(named, Signature(stream));
    }

    // The signature decides, except a generic HEIF brand (mif1), which cannot tell HEIC from AVIF: there the extension
    // decides within that family.
    private static MagickFormat Choose(MagickFormat named, MagickFormat? signature) => signature switch
    {
        null => named,
        GenericHeif when named is MagickFormat.Heic or MagickFormat.Heif or MagickFormat.Avif => named,
        GenericHeif => MagickFormat.Heic,
        { } actual => actual,
    };

    // Stands for "HEIF container, generic brand" inside Signature/Choose; never handed to ImageMagick as such.
    private const MagickFormat GenericHeif = MagickFormat.Heif;

    /// <summary>
    /// The extension for writing <paramref name="format"/>: the source's own spelling (.jpeg, .JPG) when it already
    /// names that format, otherwise the usual one.
    /// </summary>
    public static string ExtensionFor(MagickFormat format, string sourcePath)
    {
        var sourceExtension = Path.GetExtension(sourcePath);
        if (Formats.TryGetValue(sourceExtension, out var named) && named == format)
            return sourceExtension;
        return format switch
        {
            MagickFormat.Jpeg => ".jpg",
            MagickFormat.Png => ".png",
            MagickFormat.WebP => ".webp",
            MagickFormat.Avif => ".avif",
            MagickFormat.Gif => ".gif",
            MagickFormat.Bmp => ".bmp",
            MagickFormat.Tiff => ".tiff",
            _ => throw new NotSupportedException($"No output extension for {format}."),
        };
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
            [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', ..] when HeicBrands.Contains(System.Text.Encoding.ASCII.GetString(h[8..12]))
                => MagickFormat.Heic,
            [_, _, _, _, (byte)'f', (byte)'t', (byte)'y', (byte)'p', ..] when GenericHeifBrands.Contains(System.Text.Encoding.ASCII.GetString(h[8..12]))
                => GenericHeif,
            _ => null, // includes other ISO media (MP4, MOV), which must not reach libheif
        };
    }

    private static readonly HashSet<string> HeicBrands = new(StringComparer.Ordinal)
    {
        "heic", "heix", "heim", "heis", "hevc", "hevx", "hevm", "hevs",
    };

    private static readonly HashSet<string> GenericHeifBrands = new(StringComparer.Ordinal) { "mif1", "msf1", "mif2" };

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
        // A cancelled step can also surface as ObjectDisposedException (seen when cancelling during AutoOrient).
        catch (Exception ex) when (ex is MagickException or ObjectDisposedException && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }, ct);

    /// <summary>Whether the source stores its colours at 4:2:0 like camera photos: HEIC/HEIF/AVIF, or WebP that is not lossless.</summary>
    public static bool IsLossyPhotoSource(string path, MagickFormat source) =>
        source is MagickFormat.Heic or MagickFormat.Heif or MagickFormat.Avif
        || (source == MagickFormat.WebP && !IsLosslessWebP(path));

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

    /// <summary>
    /// Encodes one image in memory, then writes it to a new file, for the same reason as <see cref="Read"/>. Encoding
    /// first means no file exists while an uninterruptible encode (AVIF reports no progress) runs, so a process that exits
    /// meanwhile leaves nothing behind.
    /// </summary>
    public static void Write(IMagickImage<byte> image, string path, MagickFormat format)
    {
        var bytes = image.ToByteArray(format);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    /// <summary>
    /// Whether frames whose worst-case raw size (width x height x channels x bit depth) totals <paramref name="rawBytes"/>
    /// may be encoded in memory. Compression proves nothing (a 2.2 MB crafted TIFF of Deflate pages produced 435 MB), but
    /// raw size scales with what a page really holds: 60 black-and-white A4 scans are 65 MB raw, 60 colour ones 1.5 GB.
    /// </summary>
    /// <remarks>
    /// The guard is for TIFF pages; GIF and WebP animations have their own frames x canvas cap. Some codecs can grow past
    /// the raw size: Group 4 or fax on a checkerboard about 3x (a 1.5 MB crafted TIFF of shared strips produced 450 MB),
    /// LZW on noise about 1.4x.
    /// </remarks>
    internal static bool FitsInMemory(ulong rawBytes, MagickFormat format, CompressionMethod compression)
    {
        if (format != MagickFormat.Tiff)
            return true;
        var growth = compression switch
        {
            CompressionMethod.Group4 or CompressionMethod.Fax => 3.0, // Fax is Group 3
            CompressionMethod.LZW => 1.4,
            _ => 1.0,
        };
        return rawBytes * growth <= MaxInMemoryBytes;
    }

    /// <summary>Worst-case raw size of an image: width x height x channels x bits per channel.</summary>
    internal static ulong RawBytes(IMagickImage<byte> image) => (ulong)image.Width * image.Height * image.ChannelCount * image.Depth / 8;

    private const ulong MaxInMemoryBytes = 1UL << 30;


    /// <summary>Writes every frame (an animation or pages) to a new file through a stream.</summary>
    public static void Write(MagickImageCollection images, string path, MagickFormat format)
    {
        // The pages are encoded in memory below, outside ImageMagick's limits. Only one frame's settings decide the
        // compression of a collection write (Remove metadata chooses one that suits every page).
        var raw = images.Aggregate(0UL, (sum, image) => sum + RawBytes(image));
        if (!FitsInMemory(raw, format, images[0].Settings.Compression))
            throw new InvalidOperationException("The image has too many large pages or frames to process.");

        // Multi-page TIFF cannot be written to a .NET stream (libtiff fails linking directories); an in-memory blob works.
        var bytes = images.ToByteArray(format);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }
}
