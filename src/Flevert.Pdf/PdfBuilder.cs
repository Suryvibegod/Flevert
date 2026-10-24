using System.Globalization;
using System.Text;

namespace Flevert.Pdf;

/// <summary>
/// Makes a new PDF of one image per page with pdfium's creation API (docs/specs/pdf.md, "Ảnh → PDF"; ADR-0008). A JPEG
/// is embedded as it is (DCTDecode), after <see cref="JpegSegments.Strip(ReadOnlySpan{byte})"/> removed its metadata;
/// any other image arrives as pixels, which pdfium compresses losslessly (Flate). Each page is the image at its DPI,
/// scaled down to <see cref="A4LongSide"/> on its long side when larger, and the EXIF orientation is applied by the
/// image's matrix on the page, never by re-encoding pixels. The PDF's only metadata is Producer: Flevert.
/// </summary>
/// <remarks>
/// Every call takes pdfium's process-wide lock, so builders on several threads take turns. Everything added stays in
/// memory until <see cref="Save"/>: the JPEG bytes as they are, a bitmap as 3 bytes per pixel.
/// </remarks>
internal sealed class PdfBuilder : IDisposable
{
    /// <summary>The DPI of an image that does not say (docs/specs/pdf.md).</summary>
    public const double DefaultDpi = 150;

    /// <summary>
    /// A DPI outside 1 to 10 000 is not believed and counts as unknown: images are untrusted, and past any scanner's
    /// optical resolution a page would shrink to a speck.
    /// </summary>
    public const double MinDpi = 1;

    public const double MaxDpi = 10_000;

    /// <summary>100 MP per page, JPEG or bitmap: an A4 page at 600 DPI is 35 MP.</summary>
    public const int MaxImagePixels = 100_000_000;

    /// <summary>
    /// The most image data one document holds, counted as the JPEG bytes plus 3 bytes per bitmap pixel, all kept in
    /// memory until <see cref="Save"/>. 1.5 GB, because pdfium writes cross-reference offsets with "%010d" (chromium/8100
    /// CPDF_Creator): past 2 GB the file would be broken, and Flevert opens no PDF over 2 GB anyway. The other 512 MB
    /// cover what the saved file adds to the images: Flate grows incompressible pixels by well under 1%, and each page's
    /// dictionaries and content stream take a few hundred bytes.
    /// </summary>
    public const long MaxContentBytes = 1536L * 1024 * 1024;

    /// <summary>297 mm, the long side of A4, in points: a larger page is scaled down to it.</summary>
    public const double A4LongSide = 297 / 25.4 * PointsPerInch;

    /// <summary>
    /// The shortest side a page gets: 3 pt, the smallest page size the PDF 1.7 reference (appendix C, "Architectural
    /// limits") has viewers accept. A thinner or smaller image is centred on a white page of that side, never stretched.
    /// </summary>
    public const double MinPageSide = 3;

    private const double PointsPerInch = 72;

    /// <summary>The Producer of every PDF built here, its only metadata (docs/specs/pdf.md).</summary>
    private const string Producer = "Flevert";

    /// <summary>
    /// The document every builder starts from and pdfium adds the pages to: an empty page tree and an Info dictionary
    /// holding only Producer. pdfium has no function to set Info and keeps a loaded document's own when it saves, while
    /// FPDF_CreateNewDocument writes CreationDate and Creator "PDFium". No /ID: pdfium draws a random one for each save.
    /// </summary>
    internal static readonly byte[] EmptyDocument = MakeEmptyDocument();

    private readonly DocumentHandle _document;
    private readonly long _maxImagePixels;
    private readonly long _maxContentBytes;
    private long _contentBytes; // changed only while holding the gate

    /// <summary>
    /// A new, empty document. The limits are <see cref="PdfDocument.MaxPages"/> pages (no more than Flevert opens),
    /// <see cref="MaxImagePixels"/> per page and <see cref="MaxContentBytes"/> in all; tests make them smaller.
    /// </summary>
    public unsafe PdfBuilder(int maxPages = PdfDocument.MaxPages, long maxImagePixels = MaxImagePixels, long maxContentBytes = MaxContentBytes)
    {
        MaxPages = maxPages;
        _maxImagePixels = maxImagePixels;
        _maxContentBytes = maxContentBytes;
        lock (Pdfium.Gate)
        {
            Pdfium.EnsureInitialized();
            var source = new BytesSource(EmptyDocument);
            var native = PdfiumNative.FPDF_LoadCustomDocument(source.Access, null);
            if (native == 0)
            {
                source.Dispose();
                throw new InvalidOperationException("pdfium could not create a document.");
            }

            _document = new DocumentHandle(native, source);

            // pdfium writes only the objects it reaches from the trailer, and follows the trailer's references only to
            // objects it has already parsed (chromium/8100, ObjectTreeTraverser): unread, the Info would be left out
            // and /Info would point at nothing. Reading an entry parses it.
            fixed (byte* tag = "Producer\0"u8)
            {
                if (PdfiumNative.FPDF_GetMetaText(native, tag, null, default).Value != (nuint)((Producer.Length + 1) * sizeof(char)))
                {
                    _document.Dispose();
                    throw new InvalidOperationException("pdfium could not read the document it starts from.");
                }
            }
        }
    }

    public int PageCount { get; private set; }

    public int MaxPages { get; }

    /// <summary>FPDFPage_GenerateContent; a test replaces it to reach the path that removes a page again.</summary>
    internal Func<nint, int> GenerateContent { get; init; } = PdfiumNative.FPDFPage_GenerateContent;

    /// <summary>
    /// Adds a page showing <paramref name="jpeg"/>, its bytes embedded unchanged. <paramref name="dpi"/> is the image's
    /// own resolution, or null when it has none. Throws <see cref="InvalidDataException"/> when pdfium cannot read the
    /// JPEG's header or reads another size than <paramref name="jpeg"/> says; the document is then unchanged. pdfium
    /// reads only the header: a JPEG that will not decode (no quantization tables, say) is embedded all the same, so the
    /// caller checks that first.
    /// </summary>
    public unsafe void AddJpegPage(JpegImage jpeg, double? dpi = null)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        ThrowIfTooManyPixels(jpeg.Width, jpeg.Height);
        var size = PageSize(jpeg.Width, jpeg.Height, jpeg.Orientation, dpi);
        lock (Pdfium.Gate)
        {
            ThrowIfFull(jpeg.Bytes.Length);
            var image = NewImage();
            try
            {
                // "Inline": pdfium copies the bytes into the document, so the source may go once the call returns.
                bool loaded;
                using (var source = new BytesSource(jpeg.Bytes))
                    loaded = PdfiumNative.FPDFImageObj_LoadJpegFileInline(null, 0, image, source.Access) != 0;
                if (!loaded || !HoldsImage(image, jpeg.Width, jpeg.Height))
                    throw new InvalidDataException("The JPEG could not be read.");

                AddPage(ref image, size, jpeg.Orientation, jpeg.Bytes.Length);
            }
            finally
            {
                if (image != 0)
                    PdfiumNative.FPDFPageObj_Destroy(image);
            }
        }
    }

    /// <summary>
    /// Adds a page showing <paramref name="bgra"/> (4 bytes per pixel, blue, green, red, alpha, rows top to bottom, no
    /// padding), already upright. Transparency is flattened onto white: the page has no soft mask.
    /// </summary>
    public unsafe void AddBitmapPage(ReadOnlySpan<byte> bgra, int width, int height, double? dpi = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ThrowIfTooManyPixels(width, height);
        if (bgra.Length != (long)width * height * 4)
            throw new ArgumentException("The pixels do not match the size given.", nameof(bgra));
        var bytes = 3L * width * height; // what pdfium keeps of it: BGR, no padding
        lock (Pdfium.Gate)
            ThrowIfFull(bytes); // before the work below

        var size = PageSize(width, height, 1, dpi);
        var stride = checked((width * 3) + 3) & ~3;
        var bgr = new byte[checked(stride * height)];
        FlattenOntoWhite(bgra, width, height, bgr, stride);

        lock (Pdfium.Gate)
        {
            ThrowIfFull(bytes);
            var image = NewImage();
            try
            {
                fixed (byte* pixels = bgr)
                {
                    // pdfium copies the pixels into the image, so the bitmap and buffer may go once the call returns.
                    using var bitmap = BitmapHandle.Create(width, height, pixels, stride, PdfiumNative.BitmapBgr);
                    if (PdfiumNative.FPDFImageObj_SetBitmap(null, 0, image, bitmap.DangerousGetHandle()) == 0
                        || !HoldsImage(image, width, height))
                        throw new InvalidOperationException("pdfium could not take the image.");
                }

                AddPage(ref image, size, orientation: 1, bytes);
            }
            finally
            {
                if (image != 0)
                    PdfiumNative.FPDFPageObj_Destroy(image);
            }
        }
    }

    /// <summary>
    /// Writes the whole document to <paramref name="stream"/>. An exception from the stream is rethrown as it was; the
    /// builder stays usable. pdfium's lock is held for the whole write, so every other pdfium call in the process waits
    /// for the stream (up to 1.5 GB to a slow disk): give it a local file.
    /// </summary>
    public unsafe void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (Pdfium.Gate)
        {
            ObjectDisposedException.ThrowIf(_document.IsClosed, this);
            if (PageCount == 0)
                throw new InvalidOperationException("A PDF needs at least one page.");
            using var sink = new StreamSink(stream);
            var saved = PdfiumNative.FPDF_SaveAsCopy(_document.DangerousGetHandle(), sink.Write, PdfiumNative.SaveNoIncremental) != 0;
            sink.ThrowIfFailed();
            if (!saved)
                throw new InvalidOperationException("pdfium could not save the PDF.");
        }
    }

    public void Dispose() => _document.Dispose();

    /// <summary>
    /// The page for an image of <paramref name="pixelWidth"/> x <paramref name="pixelHeight"/> stored pixels: its size
    /// at <paramref name="dpi"/> (<see cref="DefaultDpi"/> when unknown or not believable), turned by a quarter for
    /// EXIF orientations 5 to 8, and scaled down, proportions kept, to <see cref="A4LongSide"/> on its long side.
    /// </summary>
    /// <remarks>
    /// A4 rather than Letter: its long side is the longer, so fewer pages shrink. Only the long side is bounded: a page
    /// shaped like A4 prints on Letter with "fit to page" at 94% (792 / 841.89 pt), but a wider one shrinks more: a
    /// square page of that side prints at 73% on Letter (612 / 841.89) and 71% on A4 (595.28 / 841.89).
    /// </remarks>
    internal static PdfPageSize PageSize(int pixelWidth, int pixelHeight, int orientation, double? dpi)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(orientation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(orientation, 8);
        var resolution = dpi is { } given && double.IsFinite(given) && given is >= MinDpi and <= MaxDpi ? given : DefaultDpi;
        var (across, down) = orientation >= 5 ? (pixelHeight, pixelWidth) : (pixelWidth, pixelHeight);
        var width = across * PointsPerInch / resolution;
        var height = down * PointsPerInch / resolution;
        var longest = Math.Max(width, height);
        if (longest > A4LongSide)
        {
            width *= A4LongSide / longest;
            height *= A4LongSide / longest;
        }

        return new PdfPageSize(width, height);
    }

    /// <summary>
    /// The image matrix [a b c d e f] that maps the image's unit square onto a <paramref name="page"/>-sized page so
    /// that it is seen as EXIF <paramref name="orientation"/> says. In image space the first stored row is at the top
    /// (v = 1) and the first column at the left (u = 0); page space starts at the bottom-left.
    /// </summary>
    internal static (double A, double B, double C, double D, double E, double F) Matrix(PdfPageSize page, int orientation)
    {
        var (w, h) = (page.Width, page.Height);
        return orientation switch
        {
            1 => (w, 0, 0, h, 0, 0),
            2 => (-w, 0, 0, h, w, 0),     // mirrored left to right
            3 => (-w, 0, 0, -h, w, h),    // turned 180°
            4 => (w, 0, 0, -h, 0, h),     // mirrored top to bottom
            5 => (0, -h, -w, 0, w, h),    // mirrored along the main diagonal
            6 => (0, -h, w, 0, 0, h),     // turned 90° clockwise
            7 => (0, h, w, 0, 0, 0),      // mirrored along the other diagonal
            8 => (0, h, -w, 0, w, 0),     // turned 90° anticlockwise
            _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "EXIF orientations are 1 to 8."),
        };
    }

    // Call while holding the gate, before pdfium copies anything: a page more, holding <paramref name="bytes"/> more.
    private void ThrowIfFull(long bytes)
    {
        ObjectDisposedException.ThrowIf(_document.IsClosed, this);
        if (PageCount >= MaxPages)
            throw TooManyPagesError(MaxPages);
        if (_contentBytes + bytes > _maxContentBytes)
            throw TooMuchContentError(_maxContentBytes);
    }

    private void ThrowIfTooManyPixels(int width, int height)
    {
        if ((long)width * height > _maxImagePixels)
            throw TooManyPixelsError(width, height, _maxImagePixels);
    }

    /// <summary>The error for a document past <paramref name="maxPages"/> pages, also for a batch refused before building.</summary>
    internal static PdfException TooManyPagesError(int maxPages) => new(PdfError.TooManyPages,
        string.Create(CultureInfo.InvariantCulture, $"A PDF made by Flevert has at most {maxPages} pages."));

    /// <summary>The error for images that together pass <paramref name="maxContentBytes"/>.</summary>
    internal static PdfException TooMuchContentError(long maxContentBytes)
    {
        var most = maxContentBytes >= 1 << 20
            ? string.Create(CultureInfo.InvariantCulture, $"{maxContentBytes >> 20} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{maxContentBytes} bytes");
        return new PdfException(PdfError.TooLarge, $"Together the images are too large for one PDF: Flevert puts at most {most} of images in one.");
    }

    /// <summary>The error for an image of more than <paramref name="maxImagePixels"/> pixels.</summary>
    internal static PdfException TooManyPixelsError(long width, long height, long maxImagePixels) => new(PdfError.TooLarge,
        string.Create(CultureInfo.InvariantCulture, $"The image is {width} x {height} pixels; a PDF page made by Flevert holds at most {maxImagePixels}."));

    // Call while holding the gate. The caller destroys the object unless a page took it.
    private nint NewImage()
    {
        var image = PdfiumNative.FPDFPageObj_NewImageObj(_document.DangerousGetHandle());
        return image != 0 ? image : throw new InvalidOperationException("pdfium could not create an image.");
    }

    // Call while holding the gate. FPDFImageObj_LoadJpegFileInline and _SetBitmap say TRUE even when pdfium did not
    // take the image (chromium/8100: the image is then left without a stream, and the page would be saved blank).
    // The image's pixel size is only set once pdfium has read the JPEG's header or taken the bitmap.
    private static unsafe bool HoldsImage(nint image, int width, int height)
    {
        uint actualWidth, actualHeight;
        return PdfiumNative.FPDFImageObj_GetImagePixelSize(image, &actualWidth, &actualHeight) != 0
            && actualWidth == (uint)width && actualHeight == (uint)height;
    }

    // Call while holding the gate. Adds the page last, with the image on it; the page takes the image (zeroed here) or
    // pdfium frees it, and on failure the page is removed again, so the document never keeps half a page.
    private void AddPage(ref nint image, PdfPageSize size, int orientation, long bytes)
    {
        // The image fills the page unless a side is under MinPageSide: then the page is that side and the image centred.
        var pageWidth = Math.Max(size.Width, MinPageSide);
        var pageHeight = Math.Max(size.Height, MinPageSide);
        var (a, b, c, d, e, f) = Matrix(size, orientation);
        e += (pageWidth - size.Width) / 2;
        f += (pageHeight - size.Height) / 2;
        if (PdfiumNative.FPDFImageObj_SetMatrix(image, a, b, c, d, e, f) == 0)
            throw new InvalidOperationException("pdfium could not place the image.");

        var index = PageCount;
        var added = false;
        using (var page = PageHandle.New(_document, index, pageWidth, pageHeight))
        {
            var native = page.DangerousGetHandle();
            var inserted = PdfiumNative.FPDFPage_InsertObject(native, image) != 0;
            image = 0; // owned by the page now, or already freed by pdfium
            added = inserted && GenerateContent(native) != 0;
        }

        if (!added)
        {
            PdfiumNative.FPDFPage_Delete(_document.DangerousGetHandle(), index);
            throw new InvalidOperationException("pdfium could not put the image on its page.");
        }

        PageCount++;
        _contentBytes += bytes;
    }

    // Offsets computed, not typed: pdfium would silently rebuild a wrong cross-reference table, hiding the mistake.
    private static byte[] MakeEmptyDocument()
    {
        string[] objects = ["<</Type/Catalog/Pages 2 0 R>>", "<</Type/Pages/Kids[]/Count 0>>", $"<</Producer({Producer})>>"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = pdf.Length;
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<</Size {objects.Length + 1}/Root 1 0 R/Info 3 0 R>>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static void FlattenOntoWhite(ReadOnlySpan<byte> bgra, int width, int height, Span<byte> bgr, int stride)
    {
        for (var y = 0; y < height; y++)
        {
            var source = bgra.Slice(y * width * 4, width * 4);
            var target = bgr.Slice(y * stride, width * 3);
            for (var x = 0; x < width; x++)
            {
                var alpha = source[(x * 4) + 3];
                for (var channel = 0; channel < 3; channel++)
                    target[(x * 3) + channel] = (byte)(((source[(x * 4) + channel] * alpha) + (255 * (255 - alpha)) + 127) / 255);
            }
        }
    }
}
