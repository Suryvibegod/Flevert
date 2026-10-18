namespace ConvertAnything.Core;

public sealed record BatchSummary(string Text, bool HasFailures)
{
    public static BatchSummary Describe(IReadOnlyList<FileResult> results, int total, string logDirectory)
    {
        var failed = results.Count(r => !r.Succeeded);
        var succeeded = results.Count - failed;

        if (results.Count < total)
            return new($"Cancelled after {results.Count} of {total} files.", failed > 0);

        if (failed > 0)
            // The balloon may be gone before the user reads it, so name the place rather than relying on a click.
            return new($"Done: {Files(succeeded)}, {failed} failed. Details: {logDirectory}", true);

        return results.Count == 1
            ? new($"Done: {Path.GetFileName(results[0].Output)}", false)
            : new($"Done: {Files(succeeded)}", false);
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n} files";
}
