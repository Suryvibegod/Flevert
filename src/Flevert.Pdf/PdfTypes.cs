namespace Flevert.Pdf;

/// <summary>A page's size in points (1/72 inch), as displayed: the page's own rotation is applied.</summary>
public readonly record struct PdfPageSize(double Width, double Height);

/// <summary>
/// The pixel size a page renders at, and the DPI that gives it: lower than the DPI asked for when that would pass
/// <see cref="PdfDocument.MaxRenderPixels"/> on a side.
/// </summary>
public readonly record struct PdfRenderSize(int Width, int Height, double Dpi)
{
    /// <summary>The memory the rendered BGRA pixels take.</summary>
    public long Bytes => (long)Width * Height * 4;
}

/// <summary>A rendered page: 4 bytes per pixel in the order blue, green, red, alpha, rows top to bottom, no padding.</summary>
public sealed class PdfBitmap(int width, int height, double dpi, byte[] bgra)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride => Width * 4;

    public double Dpi { get; } = dpi;

    public ReadOnlyMemory<byte> Bgra { get; } = bgra;
}

/// <summary>
/// An image a page draws (<see cref="PdfDocument.GetImages"/>): which one, by the SHA-256 of its stream data as stored,
/// its pixel size, the resolution it is drawn at, and what it is stored as.
/// </summary>
/// <param name="ObjectIndex">Its index among the page's own objects, for <see cref="PdfDocument.GetImagePixels"/>.</param>
/// <param name="Dpi">The lower of its horizontal and vertical resolution as drawn; 0 inside a form XObject, where pdfium
/// gives the size in the form's space rather than the page's.</param>
/// <param name="Filters">Its stream's filters, such as <c>DCTDecode</c>.</param>
internal sealed record PdfPageImage(int ObjectIndex, string Hash, int Width, int Height, double Dpi, int BitsPerPixel, IReadOnlyList<string> Filters)
{
    /// <summary>Its index among the page's objects, then among each form's objects down to it.</summary>
    public IReadOnlyList<int> Location { get; init; } = [];

    /// <summary>The length of its stream data as stored.</summary>
    public long DataLength { get; init; }

    /// <summary>Drawn by a form XObject: its size on the page is not known.</summary>
    public bool InForm => ObjectIndex < 0;
}

/// <summary>A smaller JPEG the PDF worker wrote of an image (<see cref="PdfWorker.ShrinkImagesAsync"/>): which image, by
/// the SHA-256 of its stored data, where the JPEG is, and its size and colours.</summary>
internal sealed record PdfShrunkImage(string Hash, string Path, int Width, int Height, bool Gray);

/// <summary>An image's pixels as pdfium decodes them: one byte a pixel when <see cref="Gray"/>, else blue, green, red.</summary>
internal sealed record PdfImagePixels(int Width, int Height, bool Gray, byte[] Pixels);

/// <summary>The limits that keep an untrusted PDF from taking the machine's memory (docs/specs/pdf.md).</summary>
internal readonly record struct PdfLimits(int MaxPages, long MaxFileBytes, int MaxRenderPixels)
{
    public static PdfLimits Default { get; } = new(PdfDocument.MaxPages, PdfDocument.MaxFileBytes, PdfDocument.MaxRenderPixels);
}

/// <summary>A PDF's encryption, as far as opening it goes.</summary>
public enum PdfEncryption
{
    /// <summary>Not encrypted.</summary>
    None,

    /// <summary>Encrypted, but it opens without a password (its author restricted printing or copying, say).</summary>
    OpensWithoutPassword,

    /// <summary>Encrypted, and it opens only with a password.</summary>
    NeedsPassword,
}

/// <summary>Why a PDF cannot be used.</summary>
public enum PdfError
{
    NotPdf,
    PasswordRequired,
    UnsupportedEncryption,
    Unreadable,
    TooManyPages,
    TooLarge,
    DamagedPage,

    /// <summary>The path holds a character qpdf cannot be given (a lone UTF-16 surrogate, legal in NTFS names).</summary>
    UnsupportedFileName,

    /// <summary>Working on the PDF needs more memory than Flevert allows a PDF tool.</summary>
    NeedsTooMuchMemory,

    /// <summary>The PDF has no pages to work on.</summary>
    NoPages,

    /// <summary>Opening the PDF or rendering one of its pages went on for longer than Flevert waits (the PDF worker was stopped).</summary>
    TooSlow,

    /// <summary>The PDF worker ended without finishing or saying why: it crashed on the file, or was ended from outside.</summary>
    ReaderFailed,

    /// <summary>The password given does not open the PDF.</summary>
    WrongPassword,

    /// <summary>qpdf repaired the PDF to read it, and what it wrote has other pages than the PDF was counted with, or does
    /// not open.</summary>
    RepairFailed,
}

/// <summary>A PDF Flevert cannot use, with a message for the user.</summary>
public sealed class PdfException : Exception
{
    public PdfException() { }

    public PdfException(string message) : base(message) { }

    public PdfException(string message, Exception innerException) : base(message, innerException) { }

    public PdfException(PdfError error, string message) : base(message) => Error = error;

    public PdfException(PdfError error, string message, Exception innerException) : base(message, innerException) => Error = error;

    public PdfError Error { get; }

    /// <summary>The same words for every PDF operation, qpdf or pdfium (docs/specs/pdf.md).</summary>
    public const string NeedsPasswordMessage = "This PDF needs a password: use Remove password first";

    /// <summary>Remove password with a password that does not open the PDF.</summary>
    public const string WrongPasswordMessage = "Wrong password: it does not open this PDF.";
}
