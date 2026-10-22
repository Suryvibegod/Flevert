using System.Globalization;

namespace Flevert.Core;

/// <summary>The progress window's lines. Files run in parallel, so the first counts finished files rather than a position.</summary>
public static class BatchProgressText
{
    /// <param name="lastStarted">The file named before this report, kept while files finish.</param>
    public static string Describe(BatchProgress progress, string? lastStarted)
    {
        if (progress.Status is { } status)
            return status;
        var count = $"{progress.Completed} of {progress.Total} done";
        return (progress.Current ?? lastStarted) is { } file ? $"{count}: {Path.GetFileName(file)}" : count;
    }

    /// <summary>The second line, for the file just named: "page 37 of 200" or "37%", or <c>null</c> when the operation reports none.</summary>
    public static string? DescribeWithin(BatchProgress progress) => progress.Within switch
    {
        null => null,
        { IsPercent: true } within => string.Create(CultureInfo.InvariantCulture, $"{within.Position}%"),
        var within => string.Create(CultureInfo.InvariantCulture, $"{within.Unit} {within.Position} of {within.Total}"),
    };

    /// <summary>Steps of the bar per file, so a file reporting its pages moves the bar on its own.</summary>
    public const int BarStepsPerFile = 1_000;

    /// <summary>
    /// Where the bar stands: one <see cref="BarStepsPerFile"/> per finished file, plus the part of the file named in
    /// <paramref name="progress"/> that is done ("page 3 of 4" has two pages done, "37%" has 37 %), so a single long file
    /// moves the bar too.
    /// </summary>
    public static (int Value, int Maximum) Bar(BatchProgress progress)
    {
        var maximum = Math.Max(progress.Total, 1) * BarStepsPerFile;
        var value = progress.Completed * BarStepsPerFile;
        if (progress.Within is { } within)
            value += (int)((long)(within.IsPercent ? within.Position : within.Position - 1) * BarStepsPerFile / within.Total);
        return (Math.Clamp(value, 0, maximum), maximum);
    }
}
