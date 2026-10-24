using System.Globalization;
using Flevert.Core;
using Flevert.Image;
using Flevert.Ocr;

namespace Flevert.Pdf;

/// <summary>
/// <c>Make searchable (OCR)</c> (docs/specs/ocr.md, v1.4.0): writes <c>&lt;name&gt; (searchable).pdf</c>, where pages
/// without enough text (&lt;= 10 characters) are rendered at 300 DPI as grayscale PNG images in the isolated worker,
/// OCRed with Tesseract to generate an invisible text layer, and overlaid onto the original PDF with Qpdf.
/// The original page content is preserved without re-encoding.
/// </summary>
public sealed class PdfMakeSearchable : IOperation
{
    public const double OcrDpi = 300;
    internal const string AlreadySearchableMessage = "This PDF already has text you can search.";

    private readonly Qpdf _qpdf;
    private readonly PdfWorker _worker;
    private readonly Tesseract _tesseract;

    public PdfMakeSearchable(Qpdf qpdf)
        : this(qpdf, PdfWorker.NextToApp, Tesseract.NextToApp)
    {
    }

    internal PdfMakeSearchable(Qpdf qpdf, PdfWorker worker, Tesseract tesseract)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(tesseract);
        _qpdf = qpdf;
        _worker = worker;
        _tesseract = tesseract;
    }

    public string Id => "pdf.make-searchable";

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert: more PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public int MaxParallel => 1;

    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);

        var pages = await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        if (pages == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");

        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);

        var root = Path.GetFullPath(ScratchRoot);
        var scratch = Path.Combine(root, $"flevert-ocr-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);

        try
        {
            var (read, ocrPages) = await _worker.RenderOcrPagesAsync(inputPath, scratch, OcrDpi, progress, ct).ConfigureAwait(false);
            PdfRepair.RequireCounted(read, pages);

            if (ocrPages.Count == 0)
                throw new OperationSkippedException(AlreadySearchableMessage);

            var ocrDone = 0;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Tesseract.MaxParallel,
                CancellationToken = ct,
            };

            var layers = new Dictionary<int, string>();
            var turns = new Dictionary<int, int>();
            await Parallel.ForEachAsync(ocrPages, parallelOptions, async (page, token) =>
            {
                var imagePath = Path.Combine(scratch, string.Create(CultureInfo.InvariantCulture, $"page-{page}.png"));
                var layerBase = Path.Combine(scratch, string.Create(CultureInfo.InvariantCulture, $"layer-{page}"));
                // A page shown turned (scanned sideways or upside down) is read upright, and turned below to show upright
                // too, so its text layer is upright as tesseract writes it.
                var turned = await ImageToText.TurnUprightAsync(_tesseract, imagePath, token).ConfigureAwait(false);
                await _tesseract.WriteTextLayerAsync(imagePath, layerBase, token).ConfigureAwait(false);

                lock (layers)
                {
                    layers[page] = layerBase + ".pdf";
                    turns[page] = turned;
                }

                var completed = Interlocked.Increment(ref ocrDone);
                progress?.Report(new FileProgress(PdfExtractText.ScannedPageUnit, completed, ocrPages.Count));
            }).ConfigureAwait(false);

            string layerPdf;
            if (ocrPages.Count == 1)
            {
                layerPdf = layers[ocrPages[0]];
            }
            else
            {
                layerPdf = Path.Combine(scratch, "combined-layers.pdf");
                var layerFiles = ocrPages.Select(page => layers[page]).ToList();
                await _qpdf.MergeAsync(layerFiles, layerPdf, ct).ConfigureAwait(false);
            }

            var toPages = PdfRemoveBlankPages.Ranges(ocrPages);
            var fromPages = ocrPages.Count == 1 ? "1" : string.Create(CultureInfo.InvariantCulture, $"1-{ocrPages.Count}");
            // A page OSD found turned is made to show upright: its /Rotate turns as far as its picture was turned, as
            // OCRmyPDF --rotate-pages does, nothing re-encoded (ocr.md, review R5). Left upside down, a page gives viewers
            // each line's words last first, and Ctrl+F finds no phrase on it. qpdf turns the pages before it lays the
            // layers over them, each as its page is shown.
            var rotations = turns.Where(turn => turn.Value != 0)
                .GroupBy(turn => turn.Value, turn => turn.Key)
                .OrderBy(group => group.Key)
                .Select(group => string.Create(CultureInfo.InvariantCulture, $"--rotate=+{group.Key}:{PdfRemoveBlankPages.Ranges(group.Order())}"))
                .ToArray();

            var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (searchable).pdf");
            var output = await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
            {
                var repaired = await _qpdf.OverlayAsync(inputPath, layerPdf, toPages, fromPages, temp, rotations, token).ConfigureAwait(false);
                await PdfRepair.CheckAsync(_qpdf, repaired, pages, temp, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (signed)
                OutputNote.Add(PdfSignature.NotValidNote);

            return output;
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
