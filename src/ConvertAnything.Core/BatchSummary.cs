namespace ConvertAnything.Core;

/// <param name="HasFailures">Shown as a warning.</param>
/// <param name="ShowsLog">The text points to the log, so clicking the notification should open it.</param>
public sealed record BatchSummary(string Text, bool HasFailures, bool ShowsLog)
{
    public static BatchSummary Describe(IReadOnlyList<FileResult> results, int total, string logDirectory)
    {
        var failed = results.Count(r => r.Failed);
        var skipped = results.Count(r => r.Skipped);
        var succeeded = results.Count - failed - skipped;

        if (results.Count < total)
            return new($"Cancelled after {results.Count} of {total} files.", failed > 0, false);

        if (skipped == results.Count)
        {
            var reasons = results.Select(r => r.Error).Distinct(StringComparer.Ordinal).ToList();
            if (skipped == 1)
                return new($"Skipped: {reasons[0]}", false, false);
            return reasons.Count == 1
                ? new($"Nothing to do: {skipped} files skipped ({reasons[0]})", false, false)
                : new($"Nothing to do: {skipped} files skipped. Details: {logDirectory}", false, true);
        }

        var text = $"Done: {FileCount(succeeded)}" + (skipped > 0 ? $", {skipped} skipped" : "");
        if (failed > 0)
            // The balloon may be gone before the user reads it, so name the place rather than relying on a click.
            return new($"{text}, {failed} failed. Details: {logDirectory}", true, true);

        return results.Count == 1
            ? new($"Done: {Path.GetFileName(results[0].Output)}", false, false)
            : new(text, false, false);
    }

    private static string FileCount(int n) => n == 1 ? "1 file" : $"{n} files";
}
