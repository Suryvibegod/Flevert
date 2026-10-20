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
