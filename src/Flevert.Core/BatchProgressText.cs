using System.Globalization;

namespace Flevert.Core;

/// <summary>The progress window's lines. Files run in parallel, so the first counts finished files rather than a position.</summary>
public static class BatchProgressText
{
    /// <param name="lastStarted">The file named before this report, kept while files finish.</param>
    public static string Describe(BatchProgress progress, string? lastStarted)
    {
        var count = $"{progress.Completed} of {progress.Total} done";
        return (progress.Current ?? lastStarted) is { } file ? $"{count}: {Path.GetFileName(file)}" : count;
    }

    /// <summary>The second line, for the file just named: "page 37 of 200", or <c>null</c> when the operation reports none.</summary>
    public static string? DescribeWithin(BatchProgress progress) =>
        progress.Within is { } within
            ? string.Create(CultureInfo.InvariantCulture, $"{within.Unit} {within.Position} of {within.Total}")
            : null;
}
