using System.Globalization;

namespace Flevert.Ocr;

/// <summary>An OCR operation failed, with a message for the user; tesseract's own words are in the inner exception.</summary>
public sealed class OcrException : Exception
{
    public OcrException()
    {
    }

    public OcrException(string message)
        : base(message)
    {
    }

    public OcrException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal const string ToolFailedMessage =
        "Flevert's OCR tool stopped on this image without finishing: the image may be damaged, or made to crash OCR tools.";

    internal const string TooSlowMessage =
        "Recognising text in this image took too long: it may be damaged, or made to stall OCR tools.";

    internal const string NeedsTooMuchMemoryMessage =
        "This image needs more memory than Flevert allows its OCR tool (1 GB): it is too large, or made to exhaust memory.";

    internal const string DiskFullMessage =
        "The disk filled up while recognising text in this image: free some space and try again.";

    internal const string MissingDataMessage =
        "Flevert's OCR data (tessdata) is missing or corrupted: reinstall Flevert (in a source build, run scripts\\fetch-deps.ps1).";
}

/// <summary>tesseract failed in a way Flevert has no better words for; the message holds the tool's own.</summary>
public sealed class TesseractException : Exception
{
    public TesseractException()
    {
    }

    public TesseractException(string message)
        : base(message)
    {
    }

    public TesseractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TesseractException(string tool, int exitCode, string detail)
        : base(string.Create(CultureInfo.InvariantCulture, $"{tool} failed (exit code {exitCode}): {detail}")) => ExitCode = exitCode;

    public int ExitCode { get; }
}
