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
/// out well under that) and 3 for PNG (RGB, the size before compression: a page of photographs compresses little). More
/// than 4 000 megapixels in all, 1 800 A4 pages at 150 DPI, is refused whatever the disk.
/// </remarks>
internal static partial class PdfRenderBudget
{
    public const long MaxTotalPixels = 4_000_000_000;

    public const string TooLargeMessage =
        "This PDF is too large to convert to pictures: its pages together would be more than 4000 megapixels.";

    /// <summary>About how many bytes the pictures of <paramref name="pixels"/> pixels take in <paramref name="format"/>.</summary>
    public static long EstimateBytes(long pixels, PdfPageImageFormat format) =>
        format == PdfPageImageFormat.Jpeg ? pixels / 2 : pixels * 3;

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

    /// <summary>The bytes this user may still write to the volume <paramref name="folder"/> is on, quotas included; <c>null</c> when Windows does not say.</summary>
    public static long? FreeBytes(string folder) =>
        GetDiskFreeSpaceExW(folder, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;

    // "350 MB", "1.2 GB".
    internal static string Describe(long bytes) => bytes >= 1L << 30
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} GB")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, bytes >> 20)} MB");

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
