using System.Globalization;

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

    internal static unsafe PdfDocument Open(string path, PdfLimits limits)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
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
    public unsafe PdfBitmap Render(int pageIndex, double dpi)
    {
        lock (Pdfium.Gate)
        {
            var size = GetRenderSize(pageIndex, dpi);
            var stride = size.Width * 4;
            // At most MaxRenderPixels² × 4 bytes; pdfium draws into it and allocates no bitmap of its own.
            var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * size.Height));
            using var page = PageHandle.Load(_handle, pageIndex);
            fixed (byte* first = pixels)
            {
                using var bitmap = BitmapHandle.Create(size.Width, size.Height, first, stride);
                var native = bitmap.DangerousGetHandle();
                if (PdfiumNative.FPDFBitmap_FillRect(native, 0, 0, size.Width, size.Height, 0xFFFFFFFF) == 0)
                    throw new InvalidOperationException("pdfium could not clear the page bitmap.");
                PdfiumNative.FPDF_RenderPageBitmap(native, page.DangerousGetHandle(), 0, 0, size.Width, size.Height, 0, PdfiumNative.RenderAnnotations);
            }

            return new PdfBitmap(size.Width, size.Height, size.Dpi, pixels);
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
        PdfiumNative.ErrSecurity => new PdfException(PdfError.UnsupportedEncryption, "This PDF is protected by an encryption Flevert cannot open."),
        PdfiumNative.ErrFile => new PdfException(PdfError.Unreadable, "The PDF could not be read from disk."),
        _ => new PdfException(PdfError.NotPdf, NotPdfMessage),
    };

    /// <summary>The same words from pdfium and qpdf for a file that does not open as a PDF.</summary>
    internal const string NotPdfMessage = "This file is not a PDF, or it is damaged.";

    internal static PdfException TooLargeError(long maxFileBytes) =>
        new(PdfError.TooLarge, $"This PDF is larger than {DescribeBytes(maxFileBytes)}, the most Flevert opens.");

    internal static string DescribeBytes(long bytes) =>
        bytes >= 1L << 30 && bytes % (1L << 30) == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes >> 30} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes");
}
