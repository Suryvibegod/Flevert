using System.Globalization;
using System.Text;
using Flevert.Core;
using Flevert.Image;
using Flevert.Ocr;

namespace Flevert.Pdf;

/// <summary>
/// <c>Convert to/Text</c> (docs/specs/pdf.md, P3; docs/specs/ocr.md): writes <c>&lt;name&gt;.txt</c> beside the PDF with
/// the text of every page, in the order pdfium reads it: UTF-8 with a byte order mark (older Notepad then reads Vietnamese
/// right), lines ended by <c>\r\n</c>, and a form feed between pages. A scanned page, one with at most
/// <see cref="ScannedChars"/> characters of text, gets the text tesseract reads in its picture instead (v1.4.0). A PDF
/// in which neither finds any text is skipped.
/// </summary>
/// <remarks>
/// pdfium reads the PDF in the worker (<see cref="PdfWorker.ExtractTextAsync"/>), as for pictures, so an untrusted PDF is
/// never parsed in the batch leader; the worker writes the file, at most 100 MB of it, with progress per page. For scanned
/// pages the worker then renders them as grey PNGs (<see cref="PdfWorker.RenderOcrPagesAsync"/>) and tesseract reads them
/// from flevert.exe, as for <see cref="PdfMakeSearchable"/>: the worker may not start processes.
/// </remarks>
public sealed class PdfExtractText : IOperation
{
    /// <summary>A page with this many characters or fewer, white space aside, is read as a scan (the worker's own test).</summary>
    internal const int ScannedChars = 10;

    /// <summary>
    /// What the progress counts while tesseract reads ("scanned page 2 of 5"), after the worker counted every page: not
    /// "page" again, which would look like the same work starting over.
    /// </summary>
    internal const string ScannedPageUnit = "scanned page";

    internal const string NoTextMessage = "This PDF has no text, and Flevert found none in the pictures of its pages.";

    private readonly PdfWorker _worker;
    private readonly Tesseract _tesseract;

    public PdfExtractText()
        : this(PdfWorker.NextToApp)
    {
    }

    /// <param name="worker">The flevert.exe that reads the PDF; tests give the one they built.</param>
    internal PdfExtractText(PdfWorker worker, Tesseract? tesseract = null)
    {
        _worker = worker;
        _tesseract = tesseract ?? Tesseract.NextToApp;
    }

    public string Id => "pdf.extract-text";

    public string MenuPath => "Convert to/Text";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // One worker at a time, as for pictures (PdfToImages.MaxParallel).
    public int MaxParallel => 1;

    /// <summary>Where the pictures of scanned pages go, each run in a private folder: the user's temporary folder, or a test's.</summary>
    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + ".txt");
        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => WriteAsync(inputPath, temp, progress, token), ct);
    }

    private async Task WriteAsync(string inputPath, string temp, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        string[]? pages;
        try
        {
            await _worker.ExtractTextAsync(inputPath, temp, progress, ct).ConfigureAwait(false);
            pages = (await File.ReadAllTextAsync(temp, ct).ConfigureAwait(false)).Split('\f');
        }
        catch (OperationSkippedException) when (OperatingSystem.IsWindows())
        {
            // No text anywhere: every page is a scan, or blank.
            pages = null;
        }

        // Flevert has tesseract for Windows only so far (docs/specs/linux.md): elsewhere the text pdfium found is all there
        // is, and a PDF without any is skipped, as before v1.4.0.
        if (!OperatingSystem.IsWindows() || (pages is not null && !pages.Any(IsScanned)))
            return;

        var (total, read) = await ReadScannedPagesAsync(inputPath, progress, ct).ConfigureAwait(false);
        if (read.Count == 0 && pages is not null)
            return;
        pages ??= [.. Enumerable.Repeat("", total)];
        var anyText = false;
        for (var i = 0; i < pages.Length; i++)
        {
            // A short page of real text ("Trang ba") is read too: what it holds stays unless the picture shows more.
            if (read.TryGetValue(i + 1, out var text) && Letters(text) > Letters(pages[i]))
                pages[i] = text + "\r\n";
            anyText |= pages[i].Trim().Length > 0;
        }

        if (!anyText)
        {
            // WriteAtomicallyAsync deletes what the worker wrote.
            throw new OperationSkippedException(NoTextMessage);
        }

        var whole = string.Join('\f', pages);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (utf8.GetPreamble().Length + utf8.GetByteCount(whole) > PdfRenderWorker.MaxTextBytes)
            throw new PdfException(PdfError.TooLarge, PdfRenderWorker.TooMuchTextMessage);
        File.Delete(temp);
        await File.WriteAllTextAsync(temp, whole, utf8, ct).ConfigureAwait(false);
    }

    /// <summary>Whether a page of text, as the worker cleans it, is read as a scan; the worker draws for tesseract the same pages.</summary>
    internal static bool IsScanned(string page) => Letters(page) <= ScannedChars;

    private static int Letters(string text) => text.Count(c => !char.IsWhiteSpace(c));

    // The number of pages, and the text tesseract reads on each scanned page by its number from 1, cleaned as the worker
    // cleans pdfium's.
    private async Task<(int Total, IReadOnlyDictionary<int, string> Read)> ReadScannedPagesAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        // Only this user may change the pictures tesseract reads: %TEMP% may let other accounts swap them.
        var scratch = Path.Combine(Path.GetFullPath(ScratchRoot), $"flevert-ocr-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);
        try
        {
            // Drawing the pages takes a moment beside reading them: the bar counts the pages tesseract has read, once.
            var (total, scanned) = await _worker.RenderOcrPagesAsync(inputPath, scratch, PdfMakeSearchable.OcrDpi, null, ct).ConfigureAwait(false);
            var read = new Dictionary<int, string>();
            var done = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Tesseract.MaxParallel, CancellationToken = ct };
            await Parallel.ForEachAsync(scanned, options, async (page, token) =>
            {
                var picture = Path.Combine(scratch, string.Create(CultureInfo.InvariantCulture, $"page-{page}.png"));
                var text = PdfRenderWorker.PageText(await ImageToText.ReadUprightAsync(_tesseract, picture, token).ConfigureAwait(false));
                lock (read)
                    read[page] = text;
                progress?.Report(new FileProgress(ScannedPageUnit, Interlocked.Increment(ref done), scanned.Count));
            }).ConfigureAwait(false);
            return (total, read);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder left in %TEMP% must not hide the result or the real error.
            }
        }
    }
}
