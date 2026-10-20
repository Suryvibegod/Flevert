using System.Globalization;

namespace Flevert.Core.Operations;

/// <summary>
/// Writes a folder of one-line page files with progress per page, the shape of a PDF split, to exercise folder outputs,
/// progress inside a file and Cancel; installed with <c>install-menu --dev</c> like <see cref="DevCopy"/>.
/// </summary>
public sealed class DevSplit(int pages, TimeSpan pageDelay) : IOperation
{
    private const int MaxFileNameLength = 255;

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
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, $"{stem} (pages)");
        var digits = pages.ToString(CultureInfo.InvariantCulture).Length;

        return OutputPath.WriteDirectoryAtomicallyAsync(desired, async (folder, token) =>
        {
            for (var page = 1; page <= pages; page++)
            {
                progress?.Report(new FileProgress("page", page, pages));
                await Task.Delay(pageDelay, token).ConfigureAwait(false);
                var name = PageName(stem, page.ToString("D" + digits, CultureInfo.InvariantCulture));
                await File.WriteAllTextAsync(Path.Combine(folder, name), $"Page {page} of {pages}\r\n", token).ConfigureAwait(false);
            }
        }, ct);
    }

    // "<name> 07.txt", with the name shortened so a long input name still gives a valid page name.
    private static string PageName(string stem, string number)
    {
        var suffix = $" {number}.txt";
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
