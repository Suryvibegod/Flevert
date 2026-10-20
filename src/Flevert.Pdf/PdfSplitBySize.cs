using System.Globalization;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Split into parts under 10 MB / 25 MB</c> (docs/specs/pdf.md, v0.2.2): the pages, in order, in as few PDFs as fit
/// under the limit of an email or a chat app, each <c>&lt;name&gt; part 1.pdf</c>… in a folder <c>&lt;name&gt; (parts)</c>
/// beside the original, written whole or not at all (ADR-0007). 1 MB is 1 000 000 bytes, as the apps count. A PDF already
/// under the limit is skipped; a single page over it fails, naming the page.
/// </summary>
/// <remarks>
/// <para>
/// Greedy by page, as Stirling-PDF's <c>SplitPdfBySizeController</c> (MIT) splits: a part takes pages while it stays under
/// the limit. Every part is written by qpdf (<see cref="Qpdf.SelectPagesAsync"/>: a new PDF of those pages, streams copied
/// as they are) and its real size measured, so the limit holds for the file the user sends, not for an estimate.
/// </para>
/// <para>
/// To write few candidates, qpdf first writes every page alone (<see cref="Qpdf.SplitPagesAsync"/>): a page's own size is
/// about what it adds to a part (fonts and images two pages share count in both, so the sum is on the safe side). A part
/// is filled by those sizes, written, measured, grown by the room really left, and cut back a page at a time in the rare
/// case it went over. Everything qpdf writes goes into a folder of its own under the temporary folder, which only this
/// user, SYSTEM and administrators can change (qpdf writes the pages by names anyone can predict); the parts are then
/// copied under their final names, created new.
/// </para>
/// </remarks>
public sealed class PdfSplitBySize : IOperation
{
    private const string FolderSuffix = " (parts)";
    private const int MaxFileNameLength = 255;

    private readonly Qpdf _qpdf;

    /// <param name="megabytes">10 or 25: the parts are each under this many million bytes.</param>
    public PdfSplitBySize(int megabytes, Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        Id = megabytes switch
        {
            10 => "pdf.split-10mb",
            25 => "pdf.split-25mb",
            _ => throw new ArgumentOutOfRangeException(nameof(megabytes), megabytes, "Use 10 or 25."),
        };
        LimitBytes = megabytes * 1_000_000L;
        _qpdf = qpdf;
    }

    public string Id { get; }

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert PDF tools (<see cref="PdfTools"/>).</summary>
    public string MenuPath => "";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // qpdf processes, one at a time per file, mostly reading and writing (PdfRotate.MaxParallel).
    public int MaxParallel => 4;

    /// <summary>Every part is smaller than this many bytes; tests lower it to keep their PDFs small.</summary>
    internal long LimitBytes { get; init; }

    /// <summary>Where the operation's private folders go: the user's temporary folder, or a test's.</summary>
    internal string ScratchRoot { get; init; } = Path.GetTempPath();

    /// <summary>The free bytes of the volume a folder is on; tests give a smaller disk.</summary>
    internal Func<string, long?> FreeSpace { get; init; } = PdfRenderBudget.FreeBytes;

    /// <summary>How many candidate parts qpdf wrote in the last run, for tests.</summary>
    internal int Written { get; private set; }

    private string Limit => string.Create(CultureInfo.InvariantCulture, $"{LimitBytes / 1_000_000.0:0.##} MB");

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var total = await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        if (total == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");
        var length = new FileInfo(inputPath).Length;
        if (length < LimitBytes)
            throw new OperationSkippedException($"This PDF is already under {Limit}: there is nothing to split.");
        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);

        var root = Path.GetFullPath(ScratchRoot);
        // Every page alone, and the parts tried: about twice the PDF.
        if (FreeSpace(root) is { } free && free < 2 * length)
            throw new IOException($"There is not enough free space on {Path.GetPathRoot(root)} to split this PDF.");
        var scratch = Path.Combine(root, $"flevert-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);
        try
        {
            var parts = await PlanAsync(inputPath, scratch, total, progress, ct).ConfigureAwait(false);
            var stem = Path.GetFileNameWithoutExtension(inputPath);
            var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, stem + FolderSuffix);
            var output = await OutputPath.WriteDirectoryAtomicallyAsync(desired, FolderSuffix, async (folder, token) =>
            {
                for (var i = 0; i < parts.Count; i++)
                    await CopyNewAsync(parts[i], Path.Combine(folder, PartName(stem, i + 1)), token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (signed)
                OutputNote.Add(PdfSignature.NotValidNote);
            return output;
        }
        finally
        {
            try
            {
                // Only qpdf's files are in it: nobody else can add to it, so nothing in it is a link.
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder, readable only by this user, SYSTEM and administrators.
            }
        }
    }

    // The parts, written in the scratch folder, in page order.
    private async Task<List<string>> PlanAsync(string inputPath, string scratch, int total, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        Written = 0;
        progress?.Report(new FileProgress("page", 1, total));
        var single = await SinglePageSizesAsync(inputPath, scratch, total, ct).ConfigureAwait(false);
        var parts = new List<string>();
        var first = 0;
        while (first < total)
        {
            // Filled by the pages' own sizes, which count what pages share in each of them.
            var last = first;
            var estimate = single[first];
            while (last + 1 < total && estimate + single[last + 1] < LimitBytes)
                estimate += single[++last];

            var (path, size) = await WritePartAsync(inputPath, scratch, first, last, ct).ConfigureAwait(false);
            // Over the limit after all: one page fewer at a time.
            while (size >= LimitBytes && last > first)
            {
                File.Delete(path);
                (path, size) = await WritePartAsync(inputPath, scratch, first, --last, ct).ConfigureAwait(false);
            }

            if (size >= LimitBytes)
                throw TooLargePage(first + 1);

            // Grown by the room it really has left, until the next try goes over.
            while (last + 1 < total)
            {
                var grown = last;
                var room = size;
                while (grown + 1 < total && room + single[grown + 1] < LimitBytes)
                    room += single[++grown];
                // At least the next page: what pages share counts in their own sizes, and may not in the part.
                grown = Math.Max(grown, last + 1);
                var (larger, largerSize) = await WritePartAsync(inputPath, scratch, first, grown, ct).ConfigureAwait(false);
                if (largerSize >= LimitBytes)
                {
                    File.Delete(larger);
                    break;
                }

                File.Delete(path);
                (path, size, last) = (larger, largerSize, grown);
            }

            parts.Add(path);
            first = last + 1;
            progress?.Report(new FileProgress("page", Math.Min(first + 1, total), total));
        }

        return parts;
    }

    private PdfException TooLargePage(int page) => new(PdfError.TooLarge, string.Create(CultureInfo.InvariantCulture,
        $"Page {page} of this PDF is {Limit} or more on its own, so it cannot go in a part under {Limit}."));

    // Each page's size as a PDF of its own, from one run of qpdf; deleted once measured.
    private async Task<long[]> SinglePageSizesAsync(string inputPath, string scratch, int total, CancellationToken ct)
    {
        var folder = Path.Combine(scratch, "pages");
        PrivateFolder.Create(folder);
        // The count was checked when qpdf read the PDF to count it; a repair is checked on each part.
        await _qpdf.SplitPagesAsync(inputPath, folder, ct).ConfigureAwait(false);
        var sizes = new long[total];
        var files = Directory.GetFiles(folder);
        foreach (var file in files)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                || page < 1 || page > total || sizes[page - 1] != 0)
            {
                throw new QpdfException($"qpdf wrote an unexpected file while splitting: {Path.GetFileName(file)}");
            }

            sizes[page - 1] = new FileInfo(file).Length;
        }

        if (files.Length != total)
            throw new QpdfException(string.Create(CultureInfo.InvariantCulture, $"qpdf wrote {files.Length} pages of the {total} it counted."));
        Directory.Delete(folder, recursive: true);
        return sizes;
    }

    // Pages first..last (from 0) as a new PDF in the scratch folder, and its size.
    private async Task<(string Path, long Size)> WritePartAsync(string inputPath, string scratch, int first, int last, CancellationToken ct)
    {
        var path = Path.Combine(scratch, $"part-{Guid.NewGuid():N}.pdf");
        var range = first == last
            ? (first + 1).ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{first + 1}-{last + 1}");
        Written++;
        var repaired = await _qpdf.SelectPagesAsync(inputPath, range, path, keepDocument: false, ct).ConfigureAwait(false);
        await PdfRepair.CheckAsync(_qpdf, repaired, last - first + 1, path, ct).ConfigureAwait(false);
        return (path, new FileInfo(path).Length);
    }

    private static async Task CopyNewAsync(string source, string destination, CancellationToken ct)
    {
        var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (from.ConfigureAwait(false))
        {
            // CreateNew: others who can write beside the input can add entries to the folder, and nothing they put there
            // is ever replaced (ADR-0007).
            var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (to.ConfigureAwait(false))
                await from.CopyToAsync(to, ct).ConfigureAwait(false);
        }
    }

    /// <summary>"&lt;name&gt; part 3.pdf", the name shortened so a long input name still gives a valid one.</summary>
    internal static string PartName(string stem, int part)
    {
        var suffix = string.Create(CultureInfo.InvariantCulture, $" part {part}.pdf");
        var room = MaxFileNameLength - suffix.Length;
        if (stem.Length > room)
        {
            stem = stem[..room];
            if (char.IsHighSurrogate(stem[^1]))
                stem = stem[..^1];
        }

        return stem + suffix;
    }
}
