namespace ConvertAnything.Core;

public sealed record BatchSummary(string Text, bool HasFailures)
{
    public static BatchSummary Describe(IReadOnlyList<FileResult> results, int total, string logDirectory)
    {
        var failed = results.Count(r => r.Failed);
        var skipped = results.Count(r => r.Skipped);
        var succeeded = results.Count - failed - skipped;

        if (results.Count < total)
            return new($"Cancelled after {results.Count} of {total} files.", failed > 0);

        if (results.Count == 1 && skipped == 1)
            return new($"Skipped: {results[0].Error}", false);

        var text = $"Done: {Files(succeeded)}" + (skipped > 0 ? $", {skipped} skipped" : "");
        if (failed > 0)
            // The balloon may be gone before the user reads it, so name the place rather than relying on a click.
            return new($"{text}, {failed} failed. Details: {logDirectory}", true);

        return results.Count == 1
            ? new($"Done: {Path.GetFileName(results[0].Output)}", false)
            : new(text, false);
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n} files";
}
