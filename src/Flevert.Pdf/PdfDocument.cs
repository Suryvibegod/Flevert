using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Pdf;

/// <summary>
/// A PDF opened with pdfium, read-only. PDFs are untrusted input (docs/specs/pdf.md): files over
/// <see cref="MaxFileBytes"/> or with more than <see cref="MaxPages"/> pages are refused, and a page never renders past
/// <see cref="MaxRenderPixels"/> on a side. Safe to use from several threads: every call takes pdfium's process-wide
/// lock, so calls run one at a time.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    public const int MaxPages = 2_000;
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The most pixels a rendered page has on each side, after DPI scaling: 400 MB of BGRA at most.</summary>
    public const int MaxRenderPixels = 10_000;

    private const double PointsPerInch = 72;

    private readonly DocumentHandle _handle;
    private readonly PdfLimits _limits;
    private readonly int _pageCount;

    private PdfDocument(DocumentHandle handle, int pageCount, PdfLimits limits)
    {
        _handle = handle;
        _pageCount = pageCount;
        _limits = limits;
    }

    /// <summary>The document, for the PDF worker's tools that edit it (<see cref="PdfToolsWorker"/>); calls hold <see cref="Pdfium.Gate"/>.</summary>
    internal DocumentHandle Handle => _handle;

    public int PageCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _pageCount;
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> (any path .NET opens, <c>\\?\</c> long paths included) for reading; others may
    /// read the file meanwhile but not change it. Throws <see cref="PdfException"/> for a PDF Flevert cannot use, and
    /// the usual file exceptions when the file cannot be opened.
    /// </summary>
    public static PdfDocument Open(string path) => Open(path, PdfLimits.Default);

    internal static PdfDocument Open(string path, PdfLimits limits)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Open(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess), limits);
    }

    /// <summary>
    /// Opens the PDF <paramref name="file"/> is open on, which the document then owns (it is closed with it, or at once
    /// when this throws). The handle should share reading only, so nobody can change the file under the parser.
    /// </summary>
    internal static unsafe PdfDocument Open(SafeFileHandle file, PdfLimits limits)
    {
        ArgumentNullException.ThrowIfNull(file);
        FileSource? source = null;
        try
        {
            var length = RandomAccess.GetLength(file);
            if (length > limits.MaxFileBytes)
                throw TooLargeError(limits.MaxFileBytes);
            source = new FileSource(file, length);

            lock (Pdfium.Gate)
            {
                Pdfium.EnsureInitialized();
                var native = PdfiumNative.FPDF_LoadCustomDocument(source.Access, null);
                if (native == 0)
                    throw LoadError(PdfiumNative.FPDF_GetLastError());

                var handle = new DocumentHandle(native, source);
                source = null; // the handle owns it now
                var pageCount = PdfiumNative.FPDF_GetPageCount(native);
                if (pageCount > limits.MaxPages || pageCount < 0)
                {
                    handle.Dispose();
                    throw new PdfException(PdfError.TooManyPages, string.Create(CultureInfo.InvariantCulture,
                        $"This PDF has {pageCount} pages; Flevert works with at most {limits.MaxPages}."));
                }

                return new PdfDocument(handle, pageCount, limits);
            }
        }
        catch
        {
            if (source is not null)
                source.Dispose();
            else
                file.Dispose(); // harmless when the document handle already closed it
            throw;
        }
    }

    /// <summary>The page's size in points, as displayed (its /Rotate applied).</summary>
    public unsafe PdfPageSize GetPageSize(int pageIndex)
    {
        lock (Pdfium.Gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, _pageCount);
            PdfiumNative.SizeF size;
            if (PdfiumNative.FPDF_GetPageSizeByIndexF(_handle.DangerousGetHandle(), pageIndex, &size) == 0
                || !float.IsFinite(size.Width) || !float.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
                throw new PdfException(PdfError.DamagedPage, $"Page {pageIndex + 1} of this PDF has no valid size.");
            return new PdfPageSize(size.Width, size.Height);
        }
    }

    /// <summary>
    /// The pixel size page <paramref name="pageIndex"/> renders at for <paramref name="dpi"/>: the DPI is lowered for a
    /// page that would pass <see cref="MaxRenderPixels"/> on a side. Lets a caller reserve memory before rendering.
    /// </summary>
    public PdfRenderSize GetRenderSize(int pageIndex, double dpi)
    {
        if (!double.IsFinite(dpi) || dpi <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "The DPI must be a positive number.");
        return Fit(GetPageSize(pageIndex), dpi, _limits.MaxRenderPixels);
    }

    /// <summary>Renders page <paramref name="pageIndex"/> on white, annotations included, at the size <see cref="GetRenderSize"/> gives.</summary>
    public PdfBitmap Render(int pageIndex, double dpi)
    {
        lock (Pdfium.Gate)
        {
            var size = GetRenderSize(pageIndex, dpi);
            // At most MaxRenderPixels² × 4 bytes; pdfium draws into it and allocates no bitmap of its own.
            var pixels = GC.AllocateUninitializedArray<byte>(checked((int)size.Bytes));
            RenderInto(pageIndex, size, pixels);
            return new PdfBitmap(size.Width, size.Height, size.Dpi, pixels);
        }
    }

    /// <summary>
    /// <see cref="Render"/> into <paramref name="pixels"/>, which holds at least <see cref="PdfRenderSize.Bytes"/> of the
    /// size <see cref="GetRenderSize"/> gives: the PDF worker draws every page into one buffer, grown for the largest page
    /// so far, instead of a new 400 MB array for each large page (review round 1, CR-11, CR-12). Returns that size; the
    /// pixels are its first <see cref="PdfRenderSize.Bytes"/> bytes, rows without padding.
    /// </summary>
    internal PdfRenderSize RenderInto(int pageIndex, double dpi, Span<byte> pixels)
    {
        lock (Pdfium.Gate)
        {
            var size = GetRenderSize(pageIndex, dpi);
            RenderInto(pageIndex, size, pixels);
            return size;
        }
    }

    private unsafe void RenderInto(int pageIndex, PdfRenderSize size, Span<byte> pixels)
    {
        var stride = size.Width * 4;
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels.Length, checked(stride * size.Height), nameof(pixels));
        using var page = PageHandle.Load(_handle, pageIndex);
        fixed (byte* first = pixels)
        {
            using var bitmap = BitmapHandle.Create(size.Width, size.Height, first, stride);
            var native = bitmap.DangerousGetHandle();
            if (PdfiumNative.FPDFBitmap_FillRect(native, 0, 0, size.Width, size.Height, 0xFFFFFFFF) == 0)
                throw new InvalidOperationException("pdfium could not clear the page bitmap.");
            PdfiumNative.FPDF_RenderPageBitmap(native, page.DangerousGetHandle(), 0, 0, size.Width, size.Height, 0, PdfiumNative.RenderAnnotations);
        }
    }

    /// <summary>An image whose stored data is larger than this is not looked at (no hash, never replaced).</summary>
    internal const uint MaxImageDataBytes = 512 * 1024 * 1024;

    /// <summary>
    /// Every image page <paramref name="pageIndex"/> draws: its own image objects, and those of the form XObjects it draws
    /// (<see cref="PdfPageImage.InForm"/>), whose size on the page is not known. An image whose data is larger than
    /// <see cref="MaxImageDataBytes"/>, or that pdfium cannot describe, is left out.
    /// </summary>
    internal unsafe IReadOnlyList<PdfPageImage> GetImages(int pageIndex)
    {
        lock (Pdfium.Gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            using var page = PageHandle.Load(_handle, pageIndex);
            var native = page.DangerousGetHandle();
            var images = new List<PdfPageImage>();
            var count = PdfiumNative.FPDFPage_CountObjects(native);
            var path = new List<int>();
            for (var index = 0; index < count; index++)
            {
                path.Add(index);
                Collect(native, PdfiumNative.FPDFPage_GetObject(native, index), index, depth: 0, path, images);
                path.RemoveAt(path.Count - 1);
            }

            return images;
        }
    }

    // Form XObjects nest; pdfium itself stops at 16 levels, and so does this.
    private const int MaxFormDepth = 16;

    // path: the item's index among the page's objects, then among each form's down to it.
    private static unsafe void Collect(nint page, nint item, int index, int depth, List<int> path, List<PdfPageImage> images)
    {
        if (item == 0)
            return;
        switch (PdfiumNative.FPDFPageObj_GetType(item))
        {
            case PdfiumNative.PageObjectImage:
                if (Describe(page, item, depth == 0 ? index : -1) is { } image)
                    images.Add(image with { Location = [.. path] });
                break;
            case PdfiumNative.PageObjectForm when depth < MaxFormDepth:
                var count = PdfiumNative.FPDFFormObj_CountObjects(item);
                for (var i = 0; i < count; i++)
                {
                    path.Add(i);
                    Collect(page, PdfiumNative.FPDFFormObj_GetObject(item, (uint)i), -1, depth + 1, path, images);
                    path.RemoveAt(path.Count - 1);
                }

                break;
        }
    }

    // The image object at location (PdfPageImage.Location) on a loaded page, or 0.
    private static nint Find(nint page, IReadOnlyList<int> location)
    {
        if (location.Count == 0 || location.Count > MaxFormDepth + 1)
            return 0;
        var item = PdfiumNative.FPDFPage_GetObject(page, location[0]);
        for (var i = 1; i < location.Count && item != 0; i++)
        {
            if (PdfiumNative.FPDFPageObj_GetType(item) != PdfiumNative.PageObjectForm || location[i] < 0)
                return 0;
            item = PdfiumNative.FPDFFormObj_GetObject(item, (uint)location[i]);
        }

        return item != 0 && PdfiumNative.FPDFPageObj_GetType(item) == PdfiumNative.PageObjectImage ? item : 0;
    }

    /// <summary>
    /// The stream data of the image at <paramref name="location"/> on page <paramref name="pageIndex"/> (one
    /// <see cref="GetImages"/> gave), as stored, filters not undone; <c>null</c> when it is not there or larger than
    /// <see cref="MaxImageDataBytes"/>.
    /// </summary>
    internal unsafe byte[]? GetImageData(int pageIndex, IReadOnlyList<int> location)
    {
        lock (Pdfium.Gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            using var page = PageHandle.Load(_handle, pageIndex);
            var image = Find(page.DangerousGetHandle(), location);
            if (image == 0)
                return null;
            var length = PdfiumNative.FPDFImageObj_GetImageDataRaw(image, null, 0);
            if (length == 0 || length > MaxImageDataBytes)
                return null;
            var data = GC.AllocateUninitializedArray<byte>((int)length);
            fixed (byte* buffer = data)
                return PdfiumNative.FPDFImageObj_GetImageDataRaw(image, buffer, length) == length ? data : null;
        }
    }

    /// <summary>The number of characters pdfium finds on page <paramref name="pageIndex"/>.</summary>
    internal int CountChars(int pageIndex)
    {
        lock (Pdfium.Gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            using var page = PageHandle.Load(_handle, pageIndex);
            var text = PdfiumNative.FPDFText_LoadPage(page.DangerousGetHandle());
            if (text == 0)
                throw new PdfException(PdfError.DamagedPage, $"Page {pageIndex + 1} of this PDF is damaged and its text cannot be read.");
            try
            {
                return Math.Max(0, PdfiumNative.FPDFText_CountChars(text));
            }
            finally
            {
                PdfiumNative.FPDFText_ClosePage(text);
            }
        }
    }

    private static unsafe PdfPageImage? Describe(nint page, nint image, int index)
    {
        PdfiumNative.ImageMetadata metadata;
        if (PdfiumNative.FPDFImageObj_GetImageMetadata(image, page, &metadata) == 0 || metadata.Width == 0 || metadata.Height == 0)
            return null;
        var length = PdfiumNative.FPDFImageObj_GetImageDataRaw(image, null, 0);
        if (length == 0 || length > MaxImageDataBytes)
            return null;
        var data = GC.AllocateUninitializedArray<byte>((int)length);
        fixed (byte* buffer = data)
        {
            if (PdfiumNative.FPDFImageObj_GetImageDataRaw(image, buffer, length) != length)
                return null;
        }

        var filters = new List<string>();
        var filterCount = PdfiumNative.FPDFImageObj_GetImageFilterCount(image);
        for (var i = 0; i < filterCount; i++)
        {
            var nameLength = PdfiumNative.FPDFImageObj_GetImageFilter(image, i, null, 0);
            if (nameLength is 0 or > 64)
                return null;
            var name = new byte[nameLength];
            fixed (byte* buffer = name)
            {
                if (PdfiumNative.FPDFImageObj_GetImageFilter(image, i, buffer, nameLength) != nameLength)
                    return null;
            }

            filters.Add(System.Text.Encoding.ASCII.GetString(name, 0, (int)nameLength - 1));
        }

        var dpi = index < 0 ? 0 : Math.Min(metadata.HorizontalDpi, metadata.VerticalDpi);
        return new PdfPageImage(index, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)),
            (int)Math.Min(metadata.Width, int.MaxValue), (int)Math.Min(metadata.Height, int.MaxValue),
            double.IsFinite(dpi) && dpi > 0 ? dpi : 0, (int)metadata.BitsPerPixel, filters)
        {
            DataLength = length,
        };
    }

    /// <summary>
    /// The pixels of image object <paramref name="objectIndex"/> of page <paramref name="pageIndex"/> (one
    /// <see cref="GetImages"/> gave), decoded by pdfium: gray, or BGR for any other colorspace (CMYK, ICC, indexed and
    /// <c>/Decode</c> applied), without its mask. <c>null</c> when pdfium cannot decode it, or gives alpha.
    /// </summary>
    internal PdfImagePixels? GetImagePixels(int pageIndex, int objectIndex) => GetImagePixels(pageIndex, [objectIndex]);

    /// <summary>As <see cref="GetImagePixels(int, int)"/>, for the image at <paramref name="location"/>, inside forms too.</summary>
    internal unsafe PdfImagePixels? GetImagePixels(int pageIndex, IReadOnlyList<int> location)
    {
        lock (Pdfium.Gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            using var page = PageHandle.Load(_handle, pageIndex);
            var image = Find(page.DangerousGetHandle(), location);
            if (image == 0)
                return null;
            var bitmap = PdfiumNative.FPDFImageObj_GetBitmap(image);
            if (bitmap == 0)
                return null;
            try
            {
                var (width, height, stride) = (PdfiumNative.FPDFBitmap_GetWidth(bitmap), PdfiumNative.FPDFBitmap_GetHeight(bitmap), PdfiumNative.FPDFBitmap_GetStride(bitmap));
                var format = PdfiumNative.FPDFBitmap_GetFormat(bitmap);
                var source = (byte*)PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
                var bytesPerPixel = format switch
                {
                    PdfiumNative.BitmapGray => 1,
                    PdfiumNative.BitmapBgr => 3,
                    PdfiumNative.BitmapBgrx => 4,
                    _ => 0,
                };
                if (bytesPerPixel == 0 || width <= 0 || height <= 0 || source is null || stride < width * bytesPerPixel)
                    return null;
                var outBytes = bytesPerPixel == 1 ? 1 : 3;
                var pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * outBytes));
                for (var y = 0; y < height; y++)
                {
                    var row = new ReadOnlySpan<byte>(source + ((long)y * stride), width * bytesPerPixel);
                    var target = pixels.AsSpan(y * width * outBytes, width * outBytes);
                    if (bytesPerPixel == outBytes)
                    {
                        row.CopyTo(target);
                    }
                    else
                    {
                        for (var x = 0; x < width; x++)
                            row.Slice(x * 4, 3).CopyTo(target.Slice(x * 3, 3));
                    }
                }

                return new PdfImagePixels(width, height, bytesPerPixel == 1, pixels);
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    /// <summary>
    /// The text of page <paramref name="pageIndex"/>, in the order pdfium reads it, lines ended as pdfium ends them
    /// (<c>\r\n</c>); an empty string for a page without text (a scan).
    /// </summary>
    public unsafe string GetText(int pageIndex)
    {
        lock (Pdfium.Gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            using var page = PageHandle.Load(_handle, pageIndex);
            var text = PdfiumNative.FPDFText_LoadPage(page.DangerousGetHandle());
            if (text == 0)
                throw new PdfException(PdfError.DamagedPage, $"Page {pageIndex + 1} of this PDF is damaged and its text cannot be read.");
            try
            {
                var count = PdfiumNative.FPDFText_CountChars(text);
                if (count < 0)
                    throw new PdfException(PdfError.DamagedPage, $"Page {pageIndex + 1} of this PDF is damaged and its text cannot be read.");
                if (count == 0)
                    return "";
                var buffer = new char[count + 1];
                int written;
                fixed (char* first = buffer)
                    written = PdfiumNative.FPDFText_GetText(text, 0, count, (ushort*)first);
                // The NUL pdfium ends it with is not text.
                return new string(buffer, 0, Math.Clamp(written - 1, 0, count));
            }
            finally
            {
                PdfiumNative.FPDFText_ClosePage(text);
            }
        }
    }

    public void Dispose() => _handle.Dispose();

    internal static PdfRenderSize Fit(PdfPageSize page, double dpi, int maxPixels)
    {
        var longest = Math.Max(page.Width, page.Height);
        if (Math.Round(longest * dpi / PointsPerInch) > maxPixels)
            dpi = maxPixels * PointsPerInch / longest;
        return new PdfRenderSize(Pixels(page.Width, dpi, maxPixels), Pixels(page.Height, dpi, maxPixels), dpi);
    }

    private static int Pixels(double points, double dpi, int maxPixels) =>
        (int)Math.Clamp(Math.Round(points * dpi / PointsPerInch), 1, maxPixels);

    private static PdfException LoadError(uint error) => error switch
    {
        PdfiumNative.ErrPassword => new PdfException(PdfError.PasswordRequired, PdfException.NeedsPasswordMessage),
        PdfiumNative.ErrSecurity => new PdfException(PdfError.UnsupportedEncryption, UnsupportedEncryptionMessage),
        PdfiumNative.ErrFile => new PdfException(PdfError.Unreadable, "The PDF could not be read from disk."),
        _ => new PdfException(PdfError.NotPdf, NotPdfMessage),
    };

    /// <summary>The same words from pdfium and qpdf for a PDF encrypted in a way neither reads (an unknown security handler).</summary>
    internal const string UnsupportedEncryptionMessage = "This PDF is protected by an encryption Flevert cannot open.";

    /// <summary>The same words from pdfium and qpdf for a file that does not open as a PDF.</summary>
    internal const string NotPdfMessage = "This file is not a PDF, or it is damaged.";

    internal static PdfException TooLargeError(long maxFileBytes) =>
        new(PdfError.TooLarge, $"This PDF is larger than {DescribeBytes(maxFileBytes)}, the most Flevert opens.");

    internal static string DescribeBytes(long bytes) =>
        bytes >= 1L << 30 && bytes % (1L << 30) == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes >> 30} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
}
