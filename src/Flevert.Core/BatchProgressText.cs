namespace Flevert.Core;

/// <summary>The progress window's line. Files run in parallel, so it counts finished files rather than a position.</summary>
public static class BatchProgressText
{
    /// <param name="lastStarted">The file named before this report, kept while files finish.</param>
    public static string Describe(BatchProgress progress, string? lastStarted)
    {
        var count = $"{progress.Completed} of {progress.Total} done";
        return (progress.Current ?? lastStarted) is { } file ? $"{count}: {Path.GetFileName(file)}" : count;
    }
}
