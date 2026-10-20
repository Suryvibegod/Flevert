using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Split into pages</c> (docs/specs/pdf.md): each page becomes a PDF of its own, <c>&lt;name&gt; 001.pdf</c>…, in a
/// folder <c>&lt;name&gt; (pages)</c> beside the original, written whole or not at all (ADR-0007). A one-page PDF still
/// gives a folder with its one page, so the result has the same shape whatever the input.
/// </summary>
/// <remarks>
/// <para>
/// qpdf counts the pages first (<see cref="Qpdf.CountPagesAsync"/>): a PDF with none, or with more than
/// <see cref="PdfDocument.MaxPages"/>, is refused before anything is written, and the count numbers the pages with as
/// many digits as it needs (at least three) and gives the progress its total.
/// </para>
/// <para>
/// qpdf then writes every page in one run (<see cref="Qpdf.SplitPagesAsync"/>), into a folder of its own inside the
/// temporary one that only this user, SYSTEM and administrators can change. qpdf creates each page by a name anyone can
/// predict and writes through whatever is there by that name; in the temporary folder, beside the input, other accounts
/// may be able to add entries, and a link put there would have qpdf write a page over a file it points to. Each page is
/// then copied under its final name, created new (<see cref="FileMode.CreateNew"/>): a copy gets the permissions of the
/// folder it is in, like any new file, where a move would keep the private ones.
/// </para>
/// <para>
/// Progress counts the pages qpdf has written so far, looked up while it runs: qpdf reports nothing until it exits.
/// </para>
/// </remarks>
public sealed class PdfSplit : IOperation
{
    private const string FolderSuffix = " (pages)";
    private const int MaxFileNameLength = 255;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly Qpdf _qpdf;

    public PdfSplit(Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
    }

    public string Id => "pdf.split";

    public string MenuPath => "Split into pages";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // Like the rotations: one single-threaded qpdf per file, mostly reading and writing (PdfRotate.MaxParallel).
    public int MaxParallel => 4;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var total = await _qpdf.CountPagesAsync(inputPath, ct).ConfigureAwait(false);
        if (total == 0)
            throw new PdfException(PdfError.NoPages, "This PDF has no pages.");
        if (total > PdfDocument.MaxPages)
        {
            throw new PdfException(PdfError.TooManyPages, string.Create(CultureInfo.InvariantCulture,
                $"This PDF has {total} pages; Flevert works with at most {PdfDocument.MaxPages}."));
        }

        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, stem + FolderSuffix);
        return await OutputPath.WriteDirectoryAtomicallyAsync(desired, FolderSuffix,
            (folder, token) => SplitIntoAsync(inputPath, folder, stem, total, progress, token), ct).ConfigureAwait(false);
    }

    private async Task SplitIntoAsync(string inputPath, string folder, string stem, int total, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new FileProgress("page", 1, total));
        var scratch = Path.Combine(folder, $".qpdf-{Guid.NewGuid():N}");
        CreatePrivateFolder(scratch);
        try
        {
            var split = _qpdf.SplitPagesAsync(inputPath, scratch, ct);
            await ReportWhileRunningAsync(split, scratch, total, progress).ConfigureAwait(false);
            await split.ConfigureAwait(false);

            var pages = WrittenPages(scratch, total);
            var digits = Math.Max(3, total.ToString(CultureInfo.InvariantCulture).Length);
            for (var page = 1; page <= total; page++)
            {
                ct.ThrowIfCancellationRequested();
                var name = PageName(stem, page.ToString("D" + digits, CultureInfo.InvariantCulture));
                await CopyNewAsync(pages[page - 1], Path.Combine(folder, name), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            // Only qpdf's pages are in it: nobody else can add to it, so nothing in it is a link to follow.
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Left in the temporary folder, which then stays, visible, beside the input (OutputPath).
            }
        }
    }

    // Never returns before qpdf has exited (the split task ends only then), so nothing still writes in the folder.
    private static async Task ReportWhileRunningAsync(Task split, string scratch, int total, IProgress<FileProgress>? progress)
    {
        var reported = 1;
        while (!split.IsCompleted)
        {
            await Task.WhenAny(split, Task.Delay(ProgressInterval, CancellationToken.None)).ConfigureAwait(false);
            if (progress is null || split.IsCompleted)
                continue;
            int written;
            try
            {
                written = Directory.EnumerateFiles(scratch, "*.pdf").Count();
            }
            catch (IOException)
            {
                continue;
            }

            // The page being written is the one after those already there.
            var position = Math.Min(written + 1, total);
            if (position > reported)
            {
                reported = position;
                progress.Report(new FileProgress("page", position, total));
            }
        }
    }

    // qpdf's pages in page order: exactly 1.pdf … total.pdf, as many digits as qpdf pads them to. Anything else means qpdf
    // did not do what it reported.
    private static string[] WrittenPages(string scratch, int total)
    {
        var pages = new string[total];
        var files = Directory.GetFiles(scratch);
        foreach (var file in files)
        {
            var number = Path.GetFileNameWithoutExtension(file);
            if (!Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
                || page < 1 || page > total || pages[page - 1] is not null)
            {
                throw new QpdfException($"qpdf wrote an unexpected file while splitting: {Path.GetFileName(file)}");
            }

            pages[page - 1] = file;
        }

        if (files.Length != total)
        {
            throw new QpdfException(string.Create(CultureInfo.InvariantCulture,
                $"qpdf wrote {files.Length} pages of the {total} it counted."));
        }

        return pages;
    }

    private static async Task CopyNewAsync(string source, string destination, CancellationToken ct)
    {
        var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (from.ConfigureAwait(false))
        {
            var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await using (to.ConfigureAwait(false))
                await from.CopyToAsync(to, ct).ConfigureAwait(false);
        }
    }

    // Created with its permissions in place (this user, SYSTEM, administrators; nothing inherited), so there is no moment
    // when another account could add to it. A random name nobody can create first.
    private static void CreatePrivateFolder(string path)
    {
        using var me = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
                 {
                     me.User!,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(path).Create(security);
    }

    // "<name> 007.pdf", with the name shortened so a long input name still gives a valid page name.
    private static string PageName(string stem, string number)
    {
        var suffix = $" {number}.pdf";
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
