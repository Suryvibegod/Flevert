using System.Globalization;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// What every v0.2.2 tool that has the PDF worker edit and save a PDF does around the worker (docs/specs/pdf.md,
/// "Yêu cầu"): qpdf counts the pages first (at most <see cref="PdfDocument.MaxPages"/>, walking the page tree), the
/// worker writes the result into the temporary file <see cref="OutputPath.WriteAtomicallyAsync"/> holds beside the input,
/// qpdf counts the result's pages (the worker's PDF must open and have the pages it should), and a signed PDF gets the
/// note that its signature is not valid in the result.
/// </summary>
internal static class PdfWorkerEdits
{
    /// <param name="pagesOut">The pages the result has, from the pages of the input.</param>
    public static async Task<string> RunAsync(
        Qpdf qpdf, PdfWorker worker, string inputPath, string suffix, PdfEdit edit, Func<int, int> pagesOut,
        IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var pages = await qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        if (pages == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");
        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + suffix + ".pdf");
        var output = await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
        {
            var read = await worker.EditAsync(inputPath, temp, edit, progress, token).ConfigureAwait(false);
            // pdfium and qpdf read the same page tree; a PDF they read differently is damaged.
            PdfRepair.RequireCounted(read, pages);
            var written = await PdfRepair.CountWrittenAsync(qpdf, temp, token).ConfigureAwait(false);
            if (written != pagesOut(pages))
            {
                throw new PdfException(PdfError.ReaderFailed, string.Create(CultureInfo.InvariantCulture,
                    $"Flevert's PDF reader wrote a result that does not have the pages it should ({written?.ToString(CultureInfo.InvariantCulture) ?? "none"} of {pagesOut(pages)}): the PDF may be damaged."));
            }
        }, ct).ConfigureAwait(false);
        if (signed)
            OutputNote.Add(PdfSignature.NotValidNote);
        return output;
    }
}

/// <summary>
/// <c>Add page numbers</c> (docs/specs/pdf.md, v0.2.2): writes <c>&lt;name&gt; (numbered).pdf</c>, every page with its
/// number in black Helvetica 10 pt about 1 cm from the edges, where the page is seen: on a page turned by its /Rotate the
/// number is upright at the bottom (or top right) of the page as shown. pdfium in the worker adds a text object to each
/// page (<c>FPDFPageObj_NewTextObj</c>, <c>FPDFPage_GenerateContent</c>); the content already there is not touched.
/// </summary>
public sealed class PdfPageNumbers : IOperation
{
    private readonly Qpdf _qpdf;
    private readonly PdfWorker _worker;
    private readonly PdfEdit _edit;

    public PdfPageNumbers(PdfPageNumberPosition position, PdfPageNumberStyle style, Qpdf qpdf)
        : this(position, style, qpdf, PdfWorker.NextToApp)
    {
    }

    internal PdfPageNumbers(PdfPageNumberPosition position, PdfPageNumberStyle style, Qpdf qpdf, PdfWorker worker)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        if (!Enum.IsDefined(position))
            throw new ArgumentOutOfRangeException(nameof(position), position, null);
        if (!Enum.IsDefined(style))
            throw new ArgumentOutOfRangeException(nameof(style), style, null);
        _edit = new PdfEdit(PdfEditKind.PageNumbers) { Position = position, Style = style };
        _qpdf = qpdf;
        _worker = worker;
    }

    public PdfPageNumberPosition Position => _edit.Position;

    public PdfPageNumberStyle Style => _edit.Style;

    public string Id => "pdf.page-numbers";

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert: more PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // One worker at a time, as for pictures (PdfToImages.MaxParallel).
    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct) =>
        PdfWorkerEdits.RunAsync(_qpdf, _worker, inputPath, " (numbered)", _edit, pages => pages, progress, ct);
}

/// <summary>
/// <c>Add watermark</c> (docs/specs/pdf.md, v0.2.2): writes <c>&lt;name&gt; (watermarked).pdf</c>, the text repeated
/// across every page in grey, 30% opaque, at 45°, its size following the page's: one mark on a scanned ID card is easy to
/// cut away. Vietnamese is written in Windows' Arial, embedded in the PDF at run time (<c>FPDFText_LoadFont</c>; Flevert
/// ships no font), so the text can be found and copied. The mark is on the page, but anyone with a PDF editor can still
/// remove it: it says what a copy is for, it does not stop editing.
/// </summary>
/// <remarks>
/// The text is required, at most <see cref="PdfEdit.MaxWatermarkLength"/> characters, without line breaks. Flevert asks
/// for it when the user chooses Add watermark… (<see cref="PdfWatermarkMenu"/>); without it each file fails at once.
/// </remarks>
public sealed class PdfWatermark : IOperation
{
    internal const string NoTextMessage =
        "Add watermark needs the text to write on the pages, which Flevert asks for when you choose Add watermark… in the Flevert menu; it was run without one here, so this PDF was left as it is.";

    internal static readonly string NoArialMessage = OperatingSystem.IsWindows()
        ? "The watermark is written in the Arial font of Windows, and Arial is missing from this computer."
        : "The watermark needs a font with Vietnamese letters, such as Liberation Sans, and fontconfig found none on this computer.";

    private readonly Qpdf _qpdf;
    private readonly PdfWorker _worker;

    /// <param name="text">The watermark; empty for none (the operation then fails each file, see remarks).</param>
    /// <exception cref="ArgumentException"><paramref name="text"/> is not empty and not a watermark (<see cref="PdfEdit.WatermarkText"/>).</exception>
    public PdfWatermark(string text, Qpdf qpdf)
        : this(text, qpdf, PdfWorker.NextToApp)
    {
    }

    internal PdfWatermark(string text, Qpdf qpdf, PdfWorker worker)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(qpdf);
        Text = text.Trim().Length == 0 ? "" : PdfEdit.WatermarkText(text) ?? throw new ArgumentException(InvalidTextMessage, nameof(text));
        _qpdf = qpdf;
        _worker = worker;
    }

    internal static readonly string InvalidTextMessage = string.Create(CultureInfo.InvariantCulture,
        $"A watermark is one line of at most {PdfEdit.MaxWatermarkLength} characters.");

    /// <summary>The text as it is written: composed (NFC), trimmed; empty when none was given.</summary>
    public string Text { get; }

    public string Id => "pdf.watermark";

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert: more PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        if (Text.Length == 0)
            throw new InvalidOperationException(NoTextMessage);
        if (SystemFont.Path is not { } font || !File.Exists(font))
            throw new InvalidOperationException(NoArialMessage);
        return PdfWorkerEdits.RunAsync(_qpdf, _worker, inputPath, " (watermarked)", new PdfEdit(PdfEditKind.Watermark) { Text = Text }, pages => pages, progress, ct);
    }
}

/// <summary>
/// <c>2 pages per sheet</c> and <c>4 pages per sheet</c> (docs/specs/pdf.md, v0.2.2): writes <c>&lt;name&gt; (2 per
/// sheet).pdf</c> or <c>(4 per sheet)</c>, the pages in reading order on A4 sheets, landscape for two side by side,
/// portrait for four two by two, each scaled to its place keeping its proportions: for printing on less paper. Form
/// fields and comments are drawn into their pages first, so the sheets show them.
/// </summary>
public sealed class PdfNUp : IOperation
{
    private readonly int _perSheet;
    private readonly Qpdf _qpdf;
    private readonly PdfWorker _worker;

    /// <param name="perSheet">2 or 4.</param>
    public PdfNUp(int perSheet, Qpdf qpdf)
        : this(perSheet, qpdf, PdfWorker.NextToApp)
    {
    }

    internal PdfNUp(int perSheet, Qpdf qpdf, PdfWorker worker)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        Id = perSheet switch
        {
            2 => "pdf.n-up-2",
            4 => "pdf.n-up-4",
            _ => throw new ArgumentOutOfRangeException(nameof(perSheet), perSheet, "Use 2 or 4."),
        };
        _perSheet = perSheet;
        _qpdf = qpdf;
        _worker = worker;
    }

    public string Id { get; }

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert: more PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct) =>
        PdfWorkerEdits.RunAsync(_qpdf, _worker, inputPath, string.Create(CultureInfo.InvariantCulture, $" ({_perSheet} per sheet)"),
            new PdfEdit(_perSheet == 2 ? PdfEditKind.TwoPerSheet : PdfEditKind.FourPerSheet), pages => (pages + _perSheet - 1) / _perSheet, progress, ct);
}

/// <summary>
/// <c>Fit pages to A4</c> (docs/specs/pdf.md, v0.2.2): writes <c>&lt;name&gt; (A4).pdf</c>, every page scaled onto A4,
/// portrait or landscape as the page is, keeping its proportions, centred. Pages already A4 (within 1 pt) are copied as
/// they are; a PDF whose every page is A4 is skipped. Letter, A3 and odd sizes print on A4 without the printer cropping.
/// </summary>
public sealed class PdfFitA4 : IOperation
{
    private readonly Qpdf _qpdf;
    private readonly PdfWorker _worker;

    public PdfFitA4(Qpdf qpdf)
        : this(qpdf, PdfWorker.NextToApp)
    {
    }

    internal PdfFitA4(Qpdf qpdf, PdfWorker worker)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
        _worker = worker;
    }

    public string Id => "pdf.fit-a4";

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert: more PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct) =>
        PdfWorkerEdits.RunAsync(_qpdf, _worker, inputPath, " (A4)", new PdfEdit(PdfEditKind.FitA4), pages => pages, progress, ct);
}

/// <summary>
/// <c>Remove blank pages</c> (docs/specs/pdf.md, v0.2.2): writes <c>&lt;name&gt; (no blank pages).pdf</c> without the empty
/// pages a two-sided scan leaves, and the notification says which: "Removed 2 blank pages: 2, 5."
/// </summary>
/// <remarks>
/// <para>
/// A page with a character (<c>FPDFText_CountChars</c>) or an annotation (<c>FPDFPage_GetAnnotCount</c>: a comment, a
/// stamp, a form field) is kept. The worker draws the others at 150 DPI and looks for marks: leaving out a 5 mm band
/// along the edges, a page is blank when no square of about 1 cm has more than 1.5% pixels darker than 245 of 255
/// (<see cref="PdfToolsWorker.IsBlank"/>). The threshold comes from Stirling-PDF's BlankPageController (MIT), which
/// counts over the whole page instead (blank at 99.9% white) and so drops a page with only initials or a stamp on it.
/// </para>
/// <para>
/// qpdf then writes the other pages (<see cref="Qpdf.SelectPagesAsync"/>, keeping the document: its information,
/// bookmarks and form stay), copied as they are. No blank page: skipped. Every page blank: an error, nothing written.
/// </para>
/// </remarks>
public sealed class PdfRemoveBlankPages : IOperation
{
    internal const string AllBlankMessage = "Every page of this PDF looks blank: nothing was removed.";

    // The notification names at most this many pages.
    private const int MaxNamed = 20;

    private readonly Qpdf _qpdf;
    private readonly PdfWorker _worker;

    public PdfRemoveBlankPages(Qpdf qpdf)
        : this(qpdf, PdfWorker.NextToApp)
    {
    }

    internal PdfRemoveBlankPages(Qpdf qpdf, PdfWorker worker)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
        _worker = worker;
    }

    public string Id => "pdf.remove-blank-pages";

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert: more PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var pages = await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        if (pages == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");
        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);
        var (read, blank) = await _worker.FindBlankPagesAsync(inputPath, progress, ct).ConfigureAwait(false);
        // The worker's page numbers go to qpdf: both must read the same pages.
        PdfRepair.RequireCounted(read, pages);
        if (blank.Count == 0)
            throw new OperationSkippedException(PdfToolsWorker.NoBlankPagesMessage);
        if (blank.Count == pages)
            throw new PdfException(PdfError.NoPages, AllBlankMessage);

        var kept = Ranges(Enumerable.Range(1, pages).Except(blank));
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (no blank pages).pdf");
        var output = await OutputPath.WriteAtomicallyAsync(desired, async (temp, token) =>
        {
            var repaired = await _qpdf.SelectPagesAsync(inputPath, kept, temp, keepDocument: true, token).ConfigureAwait(false);
            await PdfRepair.CheckAsync(_qpdf, repaired, pages - blank.Count, temp, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        OutputNote.Add(RemovedNote(blank));
        if (signed)
            OutputNote.Add(PdfSignature.NotValidNote);
        return output;
    }

    /// <summary>"Removed 1 blank page: 4." or "Removed 3 blank pages: 2, 8, 9.", the first 20 named.</summary>
    internal static string RemovedNote(IReadOnlyList<int> blank)
    {
        var named = string.Join(", ", blank.Take(MaxNamed).Select(page => page.ToString(CultureInfo.InvariantCulture)));
        var more = blank.Count > MaxNamed ? ", …" : "";
        return blank.Count == 1
            ? $"Removed 1 blank page: {named}."
            : string.Create(CultureInfo.InvariantCulture, $"Removed {blank.Count} blank pages: {named}{more}.");
    }

    /// <summary>Ascending page numbers as qpdf's list: "1-3,5,7-9".</summary>
    internal static string Ranges(IEnumerable<int> pages)
    {
        var parts = new List<string>();
        int? start = null, end = null;
        foreach (var page in pages)
        {
            if (end == page - 1)
            {
                end = page;
                continue;
            }

            if (start is { } s)
                parts.Add(s == end ? s.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{s}-{end}"));
            (start, end) = (page, page);
        }

        if (start is { } last)
            parts.Add(last == end ? last.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{last}-{end}"));
        return string.Join(',', parts);
    }
}

/// <summary>
/// <c>Extract images</c> (docs/specs/pdf.md, v0.2.2): every picture the pages draw, in a folder <c>&lt;name&gt;
/// (images)</c> beside the PDF: <c>&lt;name&gt; 001.jpg</c>…, written whole or not at all (ADR-0007). A JPEG in the PDF
/// comes out byte for byte as it is stored; any other image is decoded by pdfium in the worker and written as a PNG. An
/// image drawn in several places comes out once; images under 32 x 32 pixels (icons, rules) are left out. A PDF without
/// images is skipped.
/// </summary>
/// <remarks>
/// As for pictures of pages (P6.6): the images' size on disk is estimated before the first is written (the JPEGs as they
/// are, about a byte a pixel for the PNGs), and refused when the disk has not the room or the PNGs together pass 5 000
/// megapixels; an image over 100 megapixels that would have to be decoded is refused.
/// </remarks>
public sealed class PdfExtractImages : IOperation
{
    private const string FolderSuffix = " (images)";

    private readonly PdfWorker _worker;

    public PdfExtractImages()
        : this(PdfWorker.NextToApp)
    {
    }

    internal PdfExtractImages(PdfWorker worker) => _worker = worker;

    public string Id => "pdf.extract-images";

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert: more PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public int MaxParallel => 1;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, stem + FolderSuffix);
        return OutputPath.WriteDirectoryAtomicallyAsync(desired, FolderSuffix,
            (folder, token) => _worker.ExtractImagesAsync(inputPath, folder, progress, token), ct);
    }
}
