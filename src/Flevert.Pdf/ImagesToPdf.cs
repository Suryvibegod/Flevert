using System.Globalization;
using ImageMagick;
using Flevert.Core;
using Flevert.Image;

namespace Flevert.Pdf;

/// <summary>
/// <c>Combine into PDF</c> (docs/specs/pdf.md, "Ảnh → PDF"): every selected image, in the order the runner gives them
/// (Explorer's natural order), one page each, into <c>&lt;first name&gt; (+N).pdf</c> beside the first; one image gives
/// <c>&lt;name&gt;.pdf</c>. All or nothing (ADR-0007): an image that cannot be used fails the whole PDF, naming it.
/// </summary>
/// <remarks>
/// <para>
/// Each image takes the cheapest path that keeps it as it is:
/// <list type="bullet">
/// <item>A JPEG is embedded byte for byte once <see cref="JpegSegments"/> removed its metadata; its EXIF orientation
/// turns the page. One PDF viewers cannot show (arithmetic or lossless coding, 12-bit, CMYK without its Adobe marker:
/// <see cref="NotSupportedException"/>) is re-encoded like a HEIC. A damaged one (cut short, tables missing:
/// <see cref="InvalidDataException"/>) fails: re-encoding what ImageMagick makes of it would give a page that looks
/// complete without being so.</item>
/// <item>HEIC, HEIF, AVIF and lossy WebP, lossy photos already, are decoded, turned upright, converted to sRGB (pdfium
/// writes DeviceRGB, so a profile would be ignored), flattened onto white and written as a JPEG at quality 92, 4:2:0.</item>
/// <item>PNG, GIF, BMP, TIFF and lossless WebP stay lossless: upright, sRGB pixels that pdfium compresses with Flate,
/// transparency flattened onto white. An animation gives its first frame; a multi-page TIFF gives every page (a scanned
/// document is what it holds), its reduced-resolution thumbnails skipped.</item>
/// </list>
/// </para>
/// <para>
/// Every header is read before anything is decoded, so a batch past a PDF's limits is refused at once. Memory comes from
/// the image module's <see cref="MemoryBudget"/>: a share for each decode, and one for each page as long as the builder
/// holds it (until the PDF is written). A share that could never be granted beside the ones held fails the batch
/// instead of waiting for ever.
/// </para>
/// </remarks>
public sealed class ImagesToPdf : IBatchOperation
{
    /// <summary>The quality photos that are not JPEG already are written at (docs/specs/pdf.md).</summary>
    public const uint PhotoQuality = 92;

    private const string Unreadable = "The image could not be read: it is damaged, or not the kind of image its name says.";

    private readonly MemoryBudget _budget;
    private readonly int _maxPages;
    private readonly long _maxImagePixels;
    private readonly long _maxContentBytes;

    public ImagesToPdf()
        : this(MemoryBudget.Shared)
    {
    }

    /// <param name="budget">Shared with the image operations.</param>
    /// <param name="maxPages">The <see cref="PdfBuilder"/> limits; tests make them smaller.</param>
    internal ImagesToPdf(MemoryBudget budget, int maxPages = PdfDocument.MaxPages, long maxImagePixels = PdfBuilder.MaxImagePixels, long maxContentBytes = PdfBuilder.MaxContentBytes)
    {
        _budget = budget;
        _maxPages = maxPages;
        _maxImagePixels = maxImagePixels;
        _maxContentBytes = maxContentBytes;
    }

    private enum ImageKind
    {
        /// <summary>Embedded as it is, unless it turns out PDF viewers cannot show it.</summary>
        Jpeg,

        /// <summary>Decoded and written as a JPEG at <see cref="PhotoQuality"/>.</summary>
        Photo,

        /// <summary>Decoded and given to pdfium as pixels (Flate).</summary>
        Lossless,
    }

    public string Id => "pdf.from-images";

    public string MenuPath => "Combine into PDF";

    public IReadOnlyCollection<string> Extensions { get; } = [.. ImageFile.ReadableExtensions];

    public Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentOutOfRangeException.ThrowIfLessThan(inputPaths.Count, 1);
        // Off the caller's thread: decoding and pdfium are synchronous.
        return Task.Run(() => CombineAsync(inputPaths, progress, ct), ct);
    }

    private async Task<string> CombineAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        MagickSetup.EnsureInitialized();
        using var shares = new Shares(_budget);

        var images = new List<ImagePlan>(inputPaths.Count);
        foreach (var path in inputPaths)
        {
            ct.ThrowIfCancellationRequested();
            images.Add(await PlanAsync(path, shares, ct).ConfigureAwait(false));
        }

        if (images.Sum(image => image.Pages.Count) > _maxPages)
            throw PdfBuilder.TooManyPagesError(_maxPages);
        var lossless = images.Where(image => image.Kind == ImageKind.Lossless).SelectMany(image => image.Pages).ToList();
        if (lossless.Sum(page => page.KeptBitmapBytes) > _maxContentBytes)
            throw PdfBuilder.TooMuchContentError(_maxContentBytes);

        using var builder = new PdfBuilder(_maxPages, _maxImagePixels, _maxContentBytes);
        for (var i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new FileProgress("image", i + 1, images.Count));
            await AddAsync(builder, images[i], shares, ct).ConfigureAwait(false);
        }

        var first = inputPaths[0];
        var name = Path.GetFileNameWithoutExtension(first)
            + (inputPaths.Count == 1 ? "" : string.Create(CultureInfo.InvariantCulture, $" (+{inputPaths.Count - 1})"));
        var desired = Path.Combine(Path.GetDirectoryName(first)!, name + ".pdf");

        // Saving compresses each bitmap with Flate into a buffer of about its size.
        using (await shares.HoldAsync((ulong)lossless.Select(page => page.KeptBitmapBytes).DefaultIfEmpty(0).Max(), ct).ConfigureAwait(false))
            return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => Save(builder, temp, token), ct).ConfigureAwait(false);
    }

    // pdfium cannot be interrupted while it saves, but it writes through the stream: a cancelled token fails the next
    // write, pdfium stops, and OutputPath removes the temporary file.
    internal static Task Save(PdfBuilder builder, string temp, CancellationToken ct)
    {
        using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        builder.Save(new CancellableStream(file, ct));
        return Task.CompletedTask;
    }

    /// <summary>Reads the image's header: what path it takes, its pages, their size and DPI.</summary>
    private async Task<ImagePlan> PlanAsync(string path, Shares shares, CancellationToken ct)
    {
        // Reading the header can load the whole file (the WebP decoder does).
        using var share = await shares.HoldAsync(MemoryBudget.FileNeed(path), ct).ConfigureAwait(false);
        try
        {
            // Magick.NET refuses an empty stream as a wrong argument, not as a bad image.
            if (new FileInfo(path).Length == 0)
                throw new BatchInputException(path, "The file is empty.");
            var format = ImageFile.RealFormat(path);
            var kind = format switch
            {
                MagickFormat.Jpeg => ImageKind.Jpeg,
                MagickFormat.Heic or MagickFormat.Heif or MagickFormat.Avif => ImageKind.Photo,
                MagickFormat.WebP when !ImageFile.IsLosslessWebP(path) => ImageKind.Photo,
                _ => ImageKind.Lossless,
            };

            // Only a TIFF's frames are pages; an animation's are moments, and its first frame stands for it.
            using var frames = ImageFile.Ping(path, allFrames: format == MagickFormat.Tiff);
            var pages = new List<PagePlan>();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (format == MagickFormat.Tiff && ImageFile.IsThumbnail(frame))
                    continue;

                // An animation's frame is drawn on its canvas, which coalescing expands it to.
                var (width, height) = frame.Page is { Width: > 0, Height: > 0 } canvas && ImageFile.IsAnimationFormat(format)
                    ? (canvas.Width, canvas.Height)
                    : (frame.Width, frame.Height);
                if ((long)width * height > _maxImagePixels)
                    throw new BatchInputException(path, PdfBuilder.TooManyPixelsError(width, height, _maxImagePixels).Message);
                pages.Add(new PagePlan(index, width, height, Dpi(frame.Density), ImageFile.DecodedBytes(frame)));
            }

            if (pages.Count == 0)
                throw new BatchInputException(path, "The TIFF holds only thumbnails.");
            return new ImagePlan(path, format, kind, new FileInfo(path).Length, pages);
        }
        catch (MagickException e)
        {
            throw new BatchInputException(path, Unreadable, e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BatchInputException(path, e.Message, e);
        }
    }

    /// <summary>The image's own DPI when it says one, in inches or centimetres, the same across and down; else null.</summary>
    internal static double? Dpi(Density? density) => density switch
    {
        { Units: DensityUnit.PixelsPerInch } d when d.X == d.Y => d.X,
        { Units: DensityUnit.PixelsPerCentimeter } d when d.X == d.Y => d.X * 2.54,
        _ => null, // undefined units only give the pixels' shape; pixels of another shape: every viewer shows them square
    };

    private static async Task AddAsync(PdfBuilder builder, ImagePlan image, Shares shares, CancellationToken ct)
    {
        try
        {
            if (image.Kind == ImageKind.Jpeg && await TryAddJpegAsync(builder, image, shares, ct).ConfigureAwait(false))
                return;
            foreach (var page in image.Pages)
            {
                if (image.Kind == ImageKind.Lossless)
                    await AddLosslessAsync(builder, image, page, shares, ct).ConfigureAwait(false);
                else
                    await AddPhotoAsync(builder, image, page, shares, ct).ConfigureAwait(false);
            }
        }
        // A cancelled step can also surface as ObjectDisposedException (ImageFile.RunAsync).
        catch (Exception e) when (e is MagickException or ObjectDisposedException && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (MagickException e)
        {
            throw new BatchInputException(image.Path, Unreadable, e);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new BatchInputException(image.Path, e.Message, e);
        }
    }

    /// <summary>Embeds the JPEG as it is; false when PDF viewers cannot show it, for it to be re-encoded.</summary>
    private static async Task<bool> TryAddJpegAsync(PdfBuilder builder, ImagePlan image, Shares shares, CancellationToken ct)
    {
        if (image.FileBytes > JpegSegments.MaxBytes)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"The JPEG is too large: {image.FileBytes} bytes, and Flevert embeds at most {JpegSegments.MaxBytes}."));
        }

        JpegImage jpeg;
        // The file read, the copy without metadata and pdfium's copy of it.
        using (await shares.HoldAsync(checked(3 * (ulong)image.FileBytes), ct).ConfigureAwait(false))
        {
            try
            {
                jpeg = JpegSegments.Strip(await File.ReadAllBytesAsync(image.Path, ct).ConfigureAwait(false));
            }
            catch (NotSupportedException)
            {
                return false;
            }

            builder.AddJpegPage(jpeg, image.Pages[0].Dpi);
        }

        await shares.KeepAsync((ulong)jpeg.Bytes.Length, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task AddPhotoAsync(PdfBuilder builder, ImagePlan image, PagePlan page, Shares shares, CancellationToken ct)
    {
        JpegImage jpeg;
        // As an image operation reckons it (the decoded pixels, a converted copy, the encoder's), and the file read.
        using (await shares.HoldAsync(checked((3 * page.DecodedBytes) + (2 * (ulong)image.FileBytes)), ct).ConfigureAwait(false))
        {
            using var frames = Read(image, page, ct);
            var photo = frames[0];
            Upright(photo);
            ImageMetadata.FlattenOntoWhite(photo);
            photo.Strip();
            photo.Quality = PhotoQuality;
            // These sources store colour at 4:2:0; ImageMagick would pick 4:4:4 at this quality, a larger file for nothing.
            photo.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
            jpeg = JpegSegments.Strip(photo.ToByteArray(MagickFormat.Jpeg));
            builder.AddJpegPage(jpeg, page.Dpi);
        }

        await shares.KeepAsync((ulong)jpeg.Bytes.Length, ct).ConfigureAwait(false);
    }

    private static async Task AddLosslessAsync(PdfBuilder builder, ImagePlan image, PagePlan page, Shares shares, CancellationToken ct)
    {
        // The decoded pixels, the BGRA copy given to the builder (4 bytes a pixel), its BGR copy and pdfium's (3 each),
        // and the file read.
        var pixels = (ulong)page.Width * page.Height;
        using (await shares.HoldAsync(checked(page.DecodedBytes + (10 * pixels) + (2 * (ulong)image.FileBytes)), ct).ConfigureAwait(false))
        {
            using var frames = Read(image, page, ct);
            var picture = frames[0];
            Upright(picture);
            picture.ColorType = picture.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor; // grey and bilevel too
            byte[] bgra;
            using (var all = picture.GetPixelsUnsafe())
                bgra = all.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidOperationException("ImageMagick gave no pixels.");
            builder.AddBitmapPage(bgra, (int)picture.Width, (int)picture.Height, page.Dpi);
        }

        await shares.KeepAsync((ulong)page.KeptBitmapBytes, ct).ConfigureAwait(false);
    }

    // The frame the page shows, decoded on its own; an animation's first frame drawn on its canvas.
    private static MagickImageCollection Read(ImagePlan image, PagePlan page, CancellationToken ct)
    {
        var frames = image.Format == MagickFormat.Tiff
            ? ImageFile.Read(image.Path, maxFrames: 1, frameIndex: (uint)page.FrameIndex)
            : ImageFile.Read(image.Path, allFrames: false);
        try
        {
            if (frames.Count != 1)
                throw new InvalidDataException("The image holds no picture.");
            ImageFile.StopOnCancel(frames, ct);
            if (ImageFile.IsAnimationFormat(image.Format))
                ImageFile.Coalesce(frames, ct);
            return frames;
        }
        catch
        {
            frames.Dispose();
            throw;
        }
    }

    // Upright as the image module turns pixels, and in sRGB: pdfium writes DeviceRGB and drops any profile, so colours
    // stored in another space (Display P3, Adobe RGB, CMYK) would show wrong.
    private static void Upright(IMagickImage<byte> image)
    {
        ImageMetadata.AutoOrient(image);
        if (image.ColorSpace == ColorSpace.CMYK)
            ImageMetadata.ToSrgb(image);
        else if (image.GetColorProfile() is not null)
            image.TransformColorSpace(ColorProfiles.SRGB);
    }

    private sealed record ImagePlan(string Path, MagickFormat Format, ImageKind Kind, long FileBytes, IReadOnlyList<PagePlan> Pages);

    /// <param name="FrameIndex">The frame of the file the page shows.</param>
    /// <param name="Width">Its size as stored (before the EXIF orientation).</param>
    /// <param name="Dpi">Null when the image does not say.</param>
    /// <param name="DecodedBytes">What its pixels take once decoded (<see cref="ImageFile.DecodedBytes(IMagickImage{byte})"/>).</param>
    private sealed record PagePlan(int FrameIndex, uint Width, uint Height, double? Dpi, ulong DecodedBytes)
    {
        /// <summary>What the builder keeps of it as a bitmap: 3 bytes a pixel.</summary>
        public long KeptBitmapBytes => 3L * Width * Height;
    }

    /// <summary>
    /// The shares of the <see cref="MemoryBudget"/> one PDF holds: some for a step, the others for as long as the builder
    /// keeps a page. Used by one task at a time.
    /// </summary>
    private sealed class Shares(MemoryBudget budget) : IDisposable
    {
        private readonly List<IDisposable> _kept = [];
        private ulong _held;

        /// <summary>A share of <paramref name="bytes"/>, returned when the result is disposed.</summary>
        public async Task<IDisposable> HoldAsync(ulong bytes, CancellationToken ct)
        {
            // The budget grants in order and never more than it has: a share that does not fit beside the ones held here
            // would wait for this very operation to return them.
            var share = budget.ShareFor(bytes);
            if (_held + share > budget.Capacity)
            {
                var limit = budget.Capacity >= 1UL << 30
                    ? string.Create(CultureInfo.InvariantCulture, $"{budget.Capacity / (double)(1UL << 30):0.#} GB")
                    : string.Create(CultureInfo.InvariantCulture, $"{budget.Capacity >> 20} MB");
                throw new PdfException(PdfError.NeedsTooMuchMemory,
                    $"Together the images need more memory than Flevert allows for images ({limit}): select fewer or smaller images.");
            }

            var lease = await budget.AcquireAsync(bytes, ct).ConfigureAwait(false);
            _held += share;
            return new Held(this, lease, share);
        }

        /// <summary>A share kept until this is disposed: for a page in the builder.</summary>
        public async Task KeepAsync(ulong bytes, CancellationToken ct) => _kept.Add(await HoldAsync(bytes, ct).ConfigureAwait(false));

        public void Dispose()
        {
            foreach (var share in _kept)
                share.Dispose();
            _kept.Clear();
        }

        private sealed class Held(Shares owner, IDisposable lease, ulong share) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                owner._held -= share;
                lease.Dispose();
            }
        }
    }

    /// <summary>Writes to <paramref name="inner"/> until <paramref name="ct"/> is cancelled, then fails every write.</summary>
    private sealed class CancellableStream(Stream inner, CancellationToken ct) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ct.ThrowIfCancellationRequested();
            inner.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
