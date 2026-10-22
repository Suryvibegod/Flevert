using System.Globalization;

namespace Flevert.Core;

/// <param name="HasFailures">Shown as a warning.</param>
/// <param name="ShowsLog">The text points to the log, so clicking the notification should open it.</param>
public sealed record BatchSummary(string Text, bool HasFailures, bool ShowsLog)
{
    // A notification holds about 255 characters; the reason must leave room for the log folder.
    private const int MaxReasonLength = 120;

    public static BatchSummary Describe(BatchOutcome outcome, string logDirectory)
    {
        if (!outcome.OneOutput)
            return Describe(outcome.Results, outcome.Total, logDirectory);

        // One output from every file: no count of files, but what stopped it, naming the file when one did.
        if (outcome.Results.Count < outcome.Total)
            return new("Cancelled.", false, false);
        if (outcome.Results is [{ Failed: true, Error: { } error }])
            return new($"Failed: {Reason(error)} Details: {logDirectory}", true, true);
        return Describe(outcome.Results, outcome.Total, logDirectory);
    }

    // The first line: the runner puts the short reason there and the stack trace for the log after it.
    private static string Reason(string error)
    {
        var line = error.AsSpan()[..(error.IndexOfAny(['\r', '\n']) is var end and >= 0 ? end : error.Length)].Trim().ToString();
        if (line.Length > MaxReasonLength)
        {
            line = line[..(MaxReasonLength - 1)];
            if (char.IsHighSurrogate(line[^1]))
                line = line[..^1];
            return line + "…";
        }

        return line.EndsWith('.') || line.EndsWith('!') || line.EndsWith('?') ? line : line + ".";
    }

    public static BatchSummary Describe(IReadOnlyList<FileResult> results, int total, string logDirectory)
    {
        var failed = results.Count(r => r.Failed);
        var skipped = results.Count(r => r.Skipped);
        var succeeded = results.Count - failed - skipped;

        if (results.Count < total)
        {
            return failed > 0
                ? new($"Cancelled after {results.Count} of {total} files, {failed} failed. Details: {logDirectory}", true, true)
                : new($"Cancelled after {results.Count} of {total} files.", false, false);
        }

        if (results.Count > 0 && skipped == results.Count)
        {
            var reasons = results.Select(r => r.Error).Distinct(StringComparer.Ordinal).ToList();
            if (skipped == 1)
                return new($"Skipped: {reasons[0]}", false, false);
            return reasons.Count == 1
                ? new($"Nothing to do: {skipped} files skipped ({reasons[0]})", false, false)
                : new($"Nothing to do: {skipped} files skipped. Details: {logDirectory}", false, true);
        }

        if (succeeded == 0 && failed > 0)
        {
            // Not "Done: 0 files": nothing was done. One file alone is named, with why.
            if (results is [{ Error: { } error } only])
                return new($"Failed: {Path.GetFileName(only.Input)}: {Reason(error)} Details: {logDirectory}", true, true);
            var counts = CountOfFiles(failed) + (skipped > 0 ? $", {skipped} skipped" : "");
            return new($"Failed: {counts}. Details: {logDirectory}", true, true);
        }

        // A note about an output (OutputNote) is read with it: in full for one output, counted for several (each is in the
        // log line of its file).
        var noted = results.Count(r => r.Succeeded && r.Note is not null);
        // The sizes, added up, when every output has them (Compress): a total of some would mislead.
        var outputs = results.Where(r => r.Succeeded).ToList();
        var sizes = outputs.All(r => r.Sizes is not null)
            ? $", {SizeText(outputs.Sum(r => r.Sizes!.Value.OriginalBytes))} → {SizeText(outputs.Sum(r => r.Sizes!.Value.OutputBytes))}"
            : "";
        var text = $"Done: {CountOfFiles(succeeded)}" + sizes + (noted > 0 ? $", {noted} with a note" : "") + (skipped > 0 ? $", {skipped} skipped" : "");
        if (failed > 0)
            // The balloon may be gone before the user reads it, so name the place rather than relying on a click.
            return new($"{text}, {failed} failed. Details: {logDirectory}", true, true);

        if (results.Count == 1)
        {
            var done = $"Done: {Path.GetFileName(results[0].Output)}" +
                (results[0].Sizes is { } size ? $" ({SizeText(size.OutputBytes)}, was {SizeText(size.OriginalBytes)})" : "");
            return new(results[0].Note is { } note ? $"{done}. {Reason(note)}" : done, false, false);
        }

        return noted > 0 ? new($"{text}. Details: {logDirectory}", false, true) : new(text, false, false);
    }

    private static string CountOfFiles(int n) => n == 1 ? "1 file" : $"{n} files";

    /// <summary>
    /// A size as people read it, 1 MB being 1 000 000 bytes as everywhere in Flevert: "850 KB", "24.3 MB", "412 MB",
    /// "1.2 GB" (one decimal below 100, none above). Always rounded down, so a file of 24 960 000 bytes made to be under
    /// 25 MB never reads "25 MB" (doubt-driven review, DD-8).
    /// </summary>
    public static string SizeText(long bytes) => bytes switch
    {
        < 1_000 => "<1 KB",
        < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000} KB"),
        < 100_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 100_000 / 10.0:0.#} MB"),
        < 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 100_000_000 / 10.0:0.#} GB"),
    };
}
