using System.Globalization;

namespace Flevert.Core.Operations;

/// <summary>
/// Writes a folder of one-line page files with progress per page, the shape of a PDF split, to exercise folder outputs,
/// progress inside a file and Cancel; installed with <c>install-menu --dev</c> like <see cref="DevCopy"/>.
/// </summary>
public sealed class DevSplit(int pages, TimeSpan pageDelay) : IOperation
{
    private const string FolderSuffix = " (pages)";

    // FLEVERT_DEV_PAGE_MS sets the time per page: 0 for fast integration tests, longer to look at the progress window.
    public DevSplit()
        : this(50, TimeSpan.FromMilliseconds(
            int.TryParse(Environment.GetEnvironmentVariable("FLEVERT_DEV_PAGE_MS"), out var ms) ? Math.Clamp(ms, 0, 10_000) : 100))
    {
    }

    public string Id => "dev.split";

    public string MenuPath => "Dev/Split into pages";

    public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, stem + FolderSuffix);
        var digits = pages.ToString(CultureInfo.InvariantCulture).Length;

        return OutputPath.WriteDirectoryAtomicallyAsync(desired, FolderSuffix, async (folder, token) =>
        {
            for (var page = 1; page <= pages; page++)
            {
                progress?.Report(new FileProgress("page", page, pages));
                await Task.Delay(pageDelay, token).ConfigureAwait(false);
                var name = PageName(stem, page.ToString("D" + digits, CultureInfo.InvariantCulture));
                // CreateNew, no sharing: never overwrite or follow something already at the page's name (a link or file
                // another account planted in the output folder), the way PdfToImages writes its pages.
                var stream = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using (stream.ConfigureAwait(false))
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"Page {page} of {pages}\r\n"), token).ConfigureAwait(false);
            }
        }, ct);
    }

    // "<name> 07.txt", with the name shortened so a long input name still gives a valid page name.
    private static string PageName(string stem, string number)
    {
        var suffix = $" {number}.txt";
        return OutputPath.FitName(stem, suffix);
    }
}
