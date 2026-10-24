using System.Globalization;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>The image format <see cref="PdfToImages"/> writes each page in.</summary>
public enum PdfPageImageFormat
{
    Jpeg,
    Png,
}

/// <summary>
/// <c>Convert to/JPG images</c>, and Convert to PNG images in the Shift-only menu (docs/specs/pdf.md): renders every page with pdfium at
/// <see cref="Dpi"/> (lower for a page past <see cref="PdfDocument.MaxRenderPixels"/>) into a folder
/// <c>&lt;name&gt; (pages)</c> of <c>&lt;name&gt; 001.jpg</c>…, written whole or not at all, with progress per page.
/// </summary>
/// <remarks>
/// The PDF is read in a worker process (<see cref="PdfWorker"/>, ADR-0008): pdfium never parses an untrusted PDF inside
/// the batch leader. A page pdfium cannot read fails the whole file, naming the page: the folder is atomic, and one with a
/// page silently missing would look complete. ImageMagick, in the worker too, only encodes pixels pdfium rendered.
/// </remarks>
public sealed class PdfToImages : IOperation
{
    /// <summary>Readable on a phone and small (docs/specs/pdf.md, "Render").</summary>
    public const double Dpi = 150;

    private const string FolderSuffix = " (pages)";

    private readonly PdfPageImageFormat _format;
    private readonly double _dpi;
    private readonly PdfWorker _worker;

    public PdfToImages(PdfPageImageFormat format)
        : this(format, Dpi, PdfWorker.NextToApp)
    {
    }

    /// <param name="dpi">A lower DPI lets tests render many pages quickly; the output is named the same way.</param>
    /// <param name="worker">The flevert.exe that reads the PDF; tests give the one they built.</param>
    internal PdfToImages(PdfPageImageFormat format, double dpi, PdfWorker worker)
    {
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format), format, null);
        _format = format;
        _dpi = dpi;
        _worker = worker;
    }

    public string Id => _format == PdfPageImageFormat.Jpeg ? "pdf.to-jpg" : "pdf.to-png";

    public string MenuPath => _format == PdfPageImageFormat.Jpeg ? "Convert to/JPG images" : "Convert to/PNG images";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // One worker at a time: each may commit up to its memory cap (PdfWorker.DefaultMaxMemoryBytes), and a batch of PDFs
    // should not ask the machine for several of those at once.
    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, stem + FolderSuffix);
        // The worker writes the pages into the folder held here; it is deleted if the worker fails, stalls or is cancelled.
        return OutputPath.WriteDirectoryAtomicallyAsync(desired, FolderSuffix,
            (folder, token) => _worker.RenderAsync(inputPath, folder, _format, _dpi, progress, token), ct);
    }

    /// <summary>
    /// "&lt;name&gt; 007.jpg" for page 7 of <paramref name="total"/>, with at least three digits and as many as the count
    /// needs, and the name shortened so a long input name still gives a valid page name.
    /// </summary>
    internal static string PageName(string stem, int page, int total, PdfPageImageFormat format)
    {
        var digits = Math.Max(3, total.ToString(CultureInfo.InvariantCulture).Length);
        var suffix = string.Create(CultureInfo.InvariantCulture, $" {page.ToString("D" + digits, CultureInfo.InvariantCulture)}")
            + (format == PdfPageImageFormat.Jpeg ? ".jpg" : ".png");
        return OutputPath.FitName(stem, suffix);
    }
}

/// <summary>
/// The operations that read a PDF with pdfium, in the worker, of the Convert to submenu (<c>pdf.to-jpg</c>,
/// <c>pdf.extract-text</c>; <c>pdf.to-png</c> is in the Shift-only menu, <see cref="PdfToolMenu"/>). A list of its own beside the qpdf operations, so the catalog adds both.
/// </summary>
public static class PdfRenderOperations
{
    public static IReadOnlyList<IOperation> All { get; } =
        [new PdfToImages(PdfPageImageFormat.Jpeg), new PdfExtractText()];
}
