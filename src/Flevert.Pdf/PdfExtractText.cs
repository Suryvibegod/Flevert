using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Convert/Text</c> (docs/specs/pdf.md, P3): writes <c>&lt;name&gt;.txt</c> beside the PDF with the text of every
/// page, in the order pdfium reads it: UTF-8 with a byte order mark (older Notepad then reads Vietnamese right), lines
/// ended by <c>\r\n</c>, and a form feed between pages. A PDF with no text at all, a scan, is skipped: Flevert does not
/// recognise text in pictures.
/// </summary>
/// <remarks>
/// pdfium reads the PDF in the worker (<see cref="PdfWorker.ExtractTextAsync"/>), as for pictures, so an untrusted PDF is
/// never parsed in the batch leader; the worker writes the file, at most 100 MB of it, with progress per page.
/// </remarks>
public sealed class PdfExtractText : IOperation
{
    private readonly PdfWorker _worker;

    public PdfExtractText()
        : this(PdfWorker.NextToApp)
    {
    }

    /// <param name="worker">The flevert.exe that reads the PDF; tests give the one they built.</param>
    internal PdfExtractText(PdfWorker worker) => _worker = worker;

    public string Id => "pdf.extract-text";

    public string MenuPath => "Convert/Text";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // One worker at a time, as for pictures (PdfToImages.MaxParallel).
    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + ".txt");
        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _worker.ExtractTextAsync(inputPath, temp, progress, token), ct);
    }
}
