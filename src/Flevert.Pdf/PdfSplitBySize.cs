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
/// To guess well, qpdf first writes every page alone (<see cref="Qpdf.SplitPagesAsync"/>): a page's own size is about what
/// it adds to a part. Pages that share fonts or images each carry a copy when alone, so that pass can take many times the
/// PDF's size: the folder is watched while qpdf writes, and qpdf is stopped past four times the PDF (the guesses then come
/// from the PDF's average page) or past the free space of the disk (an error). A part is then searched for by its real
/// size: grown by the cost per page measured on the candidates written, and once one candidate fits and a larger one does
/// not, narrowed between them. After <see cref="MaxRuns"/> runs of qpdf in all, a part keeps the largest candidate that
/// fits, so a PDF of shared resources costs a few runs per part, not one per page. The parts are counted as they are
/// written: past four times the PDF, the free space of the disk, or the runs of qpdf that many parts may take, the split
/// stops there.
/// </para>
/// <para>
/// Everything qpdf writes goes into a folder of its own under the temporary folder, which only this user, SYSTEM and
/// administrators can change (qpdf writes the pages by names anyone can predict). The parts, at most four times the PDF in
/// all and only if they fit on the disk beside it, are then copied under their final names, created new.
/// </para>
/// </remarks>
public sealed class PdfSplitBySize : IOperation
{
    private const string FolderSuffix = " (parts)";

    // The most the parts, or the single pages, may take: four times the PDF.
    private const int MaxGrowth = 4;

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

    /// <summary>No menu path of its own: <see cref="PdfToolMenu"/> places it in Flevert: more PDF tools (<see cref="PdfTools"/>).</summary>
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

    /// <summary>Room left free on a disk Flevert writes to, for everything else on the machine; tests set none.</summary>
    internal long FreeMarginBytes { get; init; } = PdfRenderBudget.MarginBytes;

    /// <summary>How often the folder of single pages is measured while qpdf writes it.</summary>
    internal TimeSpan WatchEvery { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>How many candidate parts qpdf wrote in the last run, for tests.</summary>
    internal int Written { get; private set; }

    /// <summary>The runs of qpdf after which a part takes the largest candidate that fits: twice the parts, and 16.</summary>
    internal static int MaxRuns(int parts) => (2 * parts) + 16;

    // The most parts that take budget bytes in all: two parts in a row hold more than the limit (the second's first page
    // did not fit in the first), so they are at most twice the budget over the limit, and one more.
    private int MostParts(long budget) => (int)Math.Min(int.MaxValue / 4, (2 * budget / LimitBytes) + 1);

    private string Limit => string.Create(CultureInfo.InvariantCulture, $"{LimitBytes / 1_000_000.0:0.##} MB");

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var total = await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        if (total == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");
        var length = PinnedInput.Length(inputPath);
        if (length < LimitBytes)
            throw new OperationSkippedException($"This PDF is already under {Limit}: there is nothing to split.");
        var signed = await PdfSignature.IsSignedAsync(inputPath, ct).ConfigureAwait(false);

        var root = Path.GetFullPath(ScratchRoot);
        // Every page alone, and the parts tried: about twice the PDF.
        var free = FreeSpace(root);
        if (free is { } room && room - FreeMarginBytes < 2 * length)
            throw NoRoom(root);
        var scratch = Path.Combine(root, $"flevert-{Guid.NewGuid():N}");
        PrivateFolder.Create(scratch);
        try
        {
            var parts = await PlanAsync(inputPath, scratch, total, length, free, progress, ct).ConfigureAwait(false);
            var stem = Path.GetFileNameWithoutExtension(inputPath);
            var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, stem + FolderSuffix);
            // At most MaxGrowth times the PDF: PlanAsync stops past it.
            var sizes = parts.Sum(part => new FileInfo(part).Length);
            if (FreeSpace(Path.GetDirectoryName(desired)!) is { } beside && sizes + FreeMarginBytes > beside)
                throw NoRoom(desired);
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

    private static IOException NoRoom(string path) => new($"There is not enough free space on {Path.GetPathRoot(path)} to split this PDF.");

    private PdfException TooMuchShared() => new(PdfError.TooLarge, string.Create(CultureInfo.InvariantCulture,
        $"Split into parts under {Limit}, this PDF would take more than {MaxGrowth} times its size: its pages share pictures or fonts that every part must carry, so Flevert does not split it."));

    // The parts, written in the scratch folder, in page order.
    private async Task<List<string>> PlanAsync(
        string inputPath, string scratch, int total, long length, long? free, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        Written = 0;
        progress?.Report(new FileProgress("page", 1, total));
        var single = await SinglePageSizesAsync(inputPath, scratch, total, length, free, ct).ConfigureAwait(false);
        var parts = new List<string>();
        // The parts stay in the scratch folder until all are written: stopped as soon as they pass MaxGrowth times the
        // PDF or the free space of its disk, and past the runs of qpdf the most parts that budget allows may take.
        var budget = MaxGrowth * length;
        var disk = free is { } room ? room - FreeMarginBytes : long.MaxValue;
        var cap = Math.Min(budget, disk);
        var runs = MaxRuns(MostParts(budget));
        long kept = 0;
        // What a page adds to a part, until a part is measured: the PDF's average.
        var perPage = Math.Max(1.0, (double)length / total);
        var first = 0;
        while (first < total)
        {
            var remaining = total - first;
            int guess;
            if (single is not null)
            {
                // Filled by the pages' own sizes, which count what pages share in each of them: on the safe side.
                guess = 1;
                var estimate = single[first];
                while (guess < remaining && estimate + single[first + guess] < LimitBytes)
                    estimate += single[first + guess++];
            }
            else
            {
                guess = (int)Math.Clamp(LimitBytes * 0.9 / perPage, 1, remaining);
            }

            var (path, count, size) = await FillPartAsync(inputPath, scratch, first, remaining, guess, parts.Count + 1, ct).ConfigureAwait(false);
            parts.Add(path);
            kept += size;
            if (kept > cap)
                throw disk < budget ? NoRoom(scratch) : TooMuchShared();
            if (Written > runs)
                throw TooMuchShared();
            perPage = Math.Max(1.0, (double)size / count);
            first += count;
            progress?.Report(new FileProgress("page", Math.Min(first + 1, total), total));
        }

        return parts;
    }

    // The largest run of pages from first that stays under the limit, written: its path, page count and size. Grown by the
    // cost per page the candidates measure, then narrowed between the largest that fits and the smallest that does not;
    // once qpdf has run MaxRuns times, the largest that fits.
    private async Task<(string Path, int Count, long Size)> FillPartAsync(
        string inputPath, string scratch, int first, int remaining, int guess, int parts, CancellationToken ct)
    {
        (string Path, int Count, long Size)? fits = null;
        (int Count, long Size)? fitBefore = null;
        (int Count, long Size)? over = null;
        var misses = 0;
        var count = Math.Clamp(guess, 1, remaining);
        while (true)
        {
            var (path, size) = await WritePartAsync(inputPath, scratch, first, first + count - 1, ct).ConfigureAwait(false);
            if (size < LimitBytes)
            {
                if (fits is { } smaller)
                {
                    File.Delete(smaller.Path);
                    fitBefore = (smaller.Count, smaller.Size);
                }

                fits = (path, count, size);
            }
            else
            {
                File.Delete(path);
                if (count == 1)
                    throw TooLargePage(first + 1);
                over = (count, size);
                misses++;
            }

            if (fits is { } best && (best.Count == remaining || over?.Count == best.Count + 1 || Written >= MaxRuns(parts)))
                return best;

            if (fits is not { } low)
            {
                // Nothing fits yet: as many pages as the size measured allows, and at least half fewer after a second miss.
                var (high, highSize) = over!.Value;
                var scaled = (int)(high * (LimitBytes * 0.95) / highSize);
                count = Math.Clamp(misses > 1 ? Math.Min(scaled, high / 2) : scaled, 1, high - 1);
            }
            else if (over is not { } high)
            {
                // Growing: by the cost per page between the last two that fit, or by the average of the one.
                var slope = fitBefore is { } before && low.Count > before.Count
                    ? (double)(low.Size - before.Size) / (low.Count - before.Count)
                    : (double)low.Size / low.Count;
                var step = ((LimitBytes * 0.98) - low.Size) / Math.Max(1, slope);
                count = (int)Math.Clamp(low.Count + Math.Max(1, step), low.Count + 1, remaining);
            }
            else
            {
                // Between one that fits and one that does not: where the line through them reaches the limit, strictly
                // between them, and the middle every other run so the gap at least halves.
                var slope = (double)(high.Size - low.Size) / (high.Count - low.Count);
                var line = low.Count + (int)Math.Min(int.MaxValue, (LimitBytes - 1 - low.Size) / Math.Max(1, slope));
                var middle = low.Count + ((high.Count - low.Count) / 2);
                count = Math.Clamp(Written % 2 == 0 ? line : middle, low.Count + 1, high.Count - 1);
            }
        }
    }

    private PdfException TooLargePage(int page) => new(PdfError.TooLarge, string.Create(CultureInfo.InvariantCulture,
        $"Page {page} of this PDF is {Limit} or more on its own, so it cannot go in a part under {Limit}."));

    // Each page's size as a PDF of its own, from one run of qpdf; deleted once measured. Null when they take more than
    // MaxGrowth times the PDF: qpdf is stopped, and the guesses come from the average page. Past the free space of the
    // disk, qpdf is stopped too, and that is an error.
    private async Task<long[]?> SinglePageSizesAsync(string inputPath, string scratch, int total, long length, long? free, CancellationToken ct)
    {
        var folder = Path.Combine(scratch, "pages");
        PrivateFolder.Create(folder);
        var budget = MaxGrowth * length;
        var disk = free is { } room ? room - FreeMarginBytes : long.MaxValue;
        var cap = Math.Min(budget, disk);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var passed = 0;
        var watching = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(WatchEvery, stop.Token).ConfigureAwait(false);
                // A file qpdf is still writing may show less than it holds: by at most a page.
                if (FolderBytes(folder) > cap)
                {
                    Volatile.Write(ref passed, 1);
                    await stop.CancelAsync().ConfigureAwait(false);
                }
            }
        }, CancellationToken.None);
        try
        {
            // The count was checked when qpdf read the PDF to count it; a repair is checked on each part.
            await _qpdf.SplitPagesAsync(inputPath, folder, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && Volatile.Read(ref passed) == 1)
        {
            // qpdf has been stopped; the finally below stops the watch.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await watching.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped with qpdf.
            }
        }

        // Also when qpdf finished before the folder was measured past the cap.
        if (Volatile.Read(ref passed) == 1 || FolderBytes(folder) > cap)
        {
            Directory.Delete(folder, recursive: true);
            if (disk < budget)
                throw NoRoom(folder);
            return null;
        }

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

    private static long FolderBytes(string folder) => new DirectoryInfo(folder).EnumerateFiles().Sum(file => file.Length);

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
        return OutputPath.FitName(stem, suffix);
    }
}
