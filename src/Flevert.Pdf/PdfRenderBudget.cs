using System.Globalization;
using System.Runtime.InteropServices;

namespace Flevert.Pdf;

/// <summary>
/// What converting a PDF to pictures will take, checked in the worker once the PDF is open and before the first page is
/// drawn (final security audit of 0.2.0, SA-L1): the memory and time limits bound one page, so 2 000 large pages could
/// each pass them and still fill the disk.
/// </summary>
/// <remarks>
/// The pictures' size is estimated from the pixels of every page at the DPI asked for (each page's pixel size is read
/// from its box, without drawing it): about 0.5 byte a pixel for JPEG (quality 90, 4:2:0; a scanned or text page comes
/// out well under that) and 1 for PNG (pages of text and drawings compress far below the 3 bytes of raw RGB; a page of
/// photographs may pass it, which a page-by-page write error still catches), plus <see cref="MarginBytes"/> the disk
/// keeps free. More than 5 000 megapixels in all is refused whatever the disk: 2 000 A4 pages at 150 DPI, the most
/// 0.2.0 converted, are 4 350.
/// </remarks>
internal static partial class PdfRenderBudget
{
    public const long MaxTotalPixels = 5_000_000_000;

    public const string TooLargeMessage =
        "This PDF is too large to convert to pictures: its pages together would be more than 5000 megapixels.";

    /// <summary>The same limit for the pictures of the scanned pages tesseract reads (<see cref="CheckOcr"/>).</summary>
    public const string TooManyScannedPagesMessage =
        "This PDF has too many scanned pages to read: their pictures together would be more than 5000 megapixels.";

    /// <summary>What the disk is left with on top of the pictures, for the system and the folder's own bookkeeping.</summary>
    public const long MarginBytes = 64L << 20;

    /// <summary>About how many bytes the pictures of <paramref name="pixels"/> pixels take in <paramref name="format"/>, with the margin.</summary>
    public static long EstimateBytes(long pixels, PdfPageImageFormat format) =>
        (format == PdfPageImageFormat.Jpeg ? pixels / 2 : pixels) + MarginBytes;

    /// <summary>
    /// Throws when the pages are too large to convert at all; returns why they do not fit in <paramref name="freeBytes"/>,
    /// or <c>null</c> when they fit or the free space is not known.
    /// </summary>
    /// <exception cref="PdfException"><see cref="PdfError.TooLarge"/> past <see cref="MaxTotalPixels"/>.</exception>
    public static string? Check(long pixels, PdfPageImageFormat format, long? freeBytes)
    {
        if (pixels > MaxTotalPixels)
            throw new PdfException(PdfError.TooLarge, TooLargeMessage);
        var needed = EstimateBytes(pixels, format);
        return freeBytes is { } free && needed > free
            ? $"There is not enough free space for the pictures of this PDF: they need about {Describe(needed)}, and {Describe(free)} is free."
            : null;
    }

    /// <summary>
    /// As <see cref="Check"/>, for the grey PNGs of the scanned pages that tesseract reads (Make searchable, Convert to >
    /// Text): <paramref name="pixels"/> counts those pages only, and the words say what they are for.
    /// </summary>
    /// <exception cref="PdfException"><see cref="PdfError.TooLarge"/> past <see cref="MaxTotalPixels"/>.</exception>
    public static string? CheckOcr(long pixels, long? freeBytes)
    {
        if (pixels > MaxTotalPixels)
            throw new PdfException(PdfError.TooLarge, TooManyScannedPagesMessage);
        var needed = EstimateBytes(pixels, PdfPageImageFormat.Png);
        return freeBytes is { } free && needed > free
            ? $"There is not enough free space to read the scanned pages of this PDF: their pictures need about {Describe(needed)}, and {Describe(free)} is free."
            : null;
    }

    /// <summary>The bytes this user may still write to the volume <paramref name="folder"/> is on, quotas included; <c>null</c> when the system does not say.</summary>
    public static long? FreeBytes(string folder)
    {
        if (OperatingSystem.IsWindows())
            return GetDiskFreeSpaceExW(folder, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;

        // On Linux DriveInfo takes any folder and asks statvfs: the blocks an unprivileged user may still write.
        try
        {
            return new DriveInfo(folder).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // "350 MB", "1.2 GB".
    internal static string Describe(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, bytes >> 20)} MB");

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
