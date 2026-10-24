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
/// <see cref="ScannedChars"/> characters of text, gets the text tesseract reads in its picture instead (v1.4.0); one
/// tesseract cannot read keeps what pdfium found, and a note names it. A PDF in which neither finds any text is skipped,
/// or fails with tesseract's error when it could not read a page.
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

    /// <summary>What the progress counts while the worker draws the pages for tesseract ("page picture 2 of 5").</summary>
    internal const string DrawnPageUnit = "page picture";

    // The most pages a note names, as for removed blank pages.
    private const int MaxNamed = 20;

    internal const string NoTextMessage = "This PDF has no text, and Flevert found none in the pictures of its pages.";

    private readonly PdfWorker _worker;

    public PdfExtractText()
        : this(PdfWorker.NextToApp)
    {
    }

    /// <param name="worker">The flevert.exe that reads the PDF; tests give the one they built.</param>
    internal PdfExtractText(PdfWorker worker, Tesseract? tesseract = null)
    {
        _worker = worker;
        var engine = tesseract ?? Tesseract.NextToApp;
        ReadPicture = (picture, ct) => ImageToText.ReadUprightAsync(engine, picture, ct);
    }

    public string Id => "pdf.extract-text";

    public string MenuPath => "Convert to/Text";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // One worker at a time, as for pictures (PdfToImages.MaxParallel).
    public int MaxParallel => 1;

    /// <summary>Where the pictures of scanned pages go, each run in a private folder: the user's temporary folder, or a test's.</summary>
    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    /// <summary>
    /// Reads the text in the picture of a scanned page, <c>page-&lt;n&gt;.png</c>: tesseract, with the picture turned
    /// upright first. Tests give one that fails as tesseract can.
    /// </summary>
    internal Func<string, CancellationToken, Task<string>> ReadPicture { get; init; }

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

        var (total, read, unread) = await ReadScannedPagesAsync(inputPath, progress, ct).ConfigureAwait(false);
        pages ??= [.. Enumerable.Repeat("", total)];
        var anyText = false;
        var changed = false;
        for (var i = 0; i < pages.Length; i++)
        {
            // A short page of real text ("Trang ba") is read too: what it holds stays unless the picture shows more.
            if (read.TryGetValue(i + 1, out var text) && Letters(text) > Letters(pages[i]))
            {
                pages[i] = text + "\r\n";
                changed = true;
            }

            anyText |= pages[i].Trim().Length > 0;
        }

        // WriteAtomicallyAsync deletes what the worker wrote.
        if (!anyText && unread.Count > 0)
            throw ForPdf(unread.Values.First());
        if (!anyText)
            throw new OperationSkippedException(NoTextMessage);

        if (changed)
        {
            var whole = string.Join('\f', pages);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            if (utf8.GetPreamble().Length + utf8.GetByteCount(whole) > PdfRenderWorker.MaxTextBytes)
                throw new PdfException(PdfError.TooLarge, PdfRenderWorker.TooMuchTextMessage);

            // Into the file the worker made, emptied: deleting it and making it again would leave a moment in which another
            // account that may write in this folder could put its own file, or a link, at this name (review R12).
            // The worker makes it before it reads a page, so it is there when it found no text too; Truncate fails rather than
            // creates when it is gone. The writer starts with the byte order mark.
            var options = new FileStreamOptions { Mode = FileMode.Truncate, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            var stream = new FileStream(temp, options);
            await using (stream.ConfigureAwait(false))
            {
                var writer = new StreamWriter(stream, utf8);
                await using (writer.ConfigureAwait(false))
                    await writer.WriteAsync(whole.AsMemory(), ct).ConfigureAwait(false);
            }
        }

        if (unread.Count > 0)
            OutputNote.Add(UnreadNote([.. unread.Keys], ForPdf(unread.Values.First()).Message));
    }

    /// <summary>
    /// "The text of scanned page 2 could not be read. &lt;why&gt;" or "… scanned pages 2, 4 …", the first 20 named: the
    /// pages keep what pdfium found on them.
    /// </summary>
    internal static string UnreadNote(IReadOnlyList<int> pages, string why)
    {
        var named = string.Join(", ", pages.Take(MaxNamed).Select(page => page.ToString(CultureInfo.InvariantCulture)));
        var more = pages.Count > MaxNamed ? ", …" : "";
        var which = pages.Count == 1 ? $"page {named}" : $"pages {named}{more}";
        return $"The text of scanned {which} could not be read. {why}";
    }

    // Tesseract's words are about the picture it was given ("this image"); the user gave a PDF.
    private static Exception ForPdf(Exception e)
    {
        if (e is not OcrException ocr)
            return e;
        var message = ocr.Message switch
        {
            OcrException.ToolFailedMessage =>
                "Flevert's OCR tool stopped on a page of this PDF without finishing: the page may be damaged, or made to crash OCR tools.",
            OcrException.TooSlowMessage =>
                "Recognising text in a page of this PDF took too long: it may be damaged, or made to stall OCR tools.",
            OcrException.NeedsTooMuchMemoryMessage =>
                "A page of this PDF needs more memory than Flevert allows its OCR tool (1 GB): it is too large, or made to exhaust memory.",
            OcrException.DiskFullMessage => "The disk filled up while recognising text in this PDF: free some space and try again.",
            _ => null,
        };
        return message is null ? e : new OcrException(message, ocr);
    }

    private static bool IsScanned(string page) => Letters(page) <= ScannedChars;

    private static int Letters(string text) => text.Count(c => !char.IsWhiteSpace(c));

    // The number of pages; the text tesseract reads on each scanned page by its number from 1, cleaned as the worker cleans
    // pdfium's; and why it could not read the others, in page order.
    private async Task<(int Total, IReadOnlyDictionary<int, string> Read, SortedDictionary<int, Exception> Unread)> ReadScannedPagesAsync(
        string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        // Only this user may change the pictures tesseract reads: %TEMP% may let other accounts swap them.
        var scratch = Path.Combine(Path.GetFullPath(ScratchRoot), $"flevert-ocr-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);
        try
        {
            // Drawing 300 DPI pictures takes a while: counted as pictures, so the bar neither sits still nor seems to read
            // the pages again.
            var drawing = progress is null ? null : new DrawnPages(progress);
            var (total, scanned) = await _worker.RenderOcrPagesAsync(inputPath, scratch, PdfMakeSearchable.OcrDpi, drawing, ct).ConfigureAwait(false);
            var read = new Dictionary<int, string>();
            var unread = new SortedDictionary<int, Exception>();
            var done = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Tesseract.MaxParallel, CancellationToken = ct };
            await Parallel.ForEachAsync(scanned, options, async (page, token) =>
            {
                var picture = Path.Combine(scratch, string.Create(CultureInfo.InvariantCulture, $"page-{page}.png"));
                try
                {
                    var text = PdfRenderWorker.PageText(await ReadPicture(picture, token).ConfigureAwait(false));
                    lock (read)
                        read[page] = text;
                }
                catch (Exception e) when (e is OcrException or FileNotFoundException or DirectoryNotFoundException)
                {
                    // One page tesseract cannot read (too slow, too large, a crash, tesseract or its data missing) keeps
                    // what pdfium found on it, as before v1.4.0, rather than failing every other page (review R7).
                    lock (unread)
                        unread[page] = e;
                }

                progress?.Report(new FileProgress(ScannedPageUnit, Interlocked.Increment(ref done), scanned.Count));
            }).ConfigureAwait(false);
            return (total, read, unread);
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

    // The worker's "page 3 of 10" while it draws the pictures, as "page picture 3 of 10".
    private sealed class DrawnPages(IProgress<FileProgress> progress) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) =>
            progress.Report(value.Unit == "page" ? new FileProgress(DrawnPageUnit, value.Position, value.Total) : value);
    }
}
