namespace Flevert.Core;

/// <summary>
/// How far an operation is inside one file, shown under the batch line as "<see cref="Unit"/> <see cref="Position"/> of
/// <see cref="Total"/>", e.g. "page 37 of 200", or as "37%" for work without parts to count (<see cref="Percent"/>). See
/// ADR-0007.
/// </summary>
public sealed record FileProgress
{
    private const string PercentUnit = "%";

    /// <param name="unit">What is counted, as a singular English noun in lower case: "page".</param>
    /// <param name="position">The one being worked on, from 1 to <paramref name="total"/>.</param>
    /// <param name="total">How many the file has, at least 1.</param>
    public FileProgress(string unit, int position, int total)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentOutOfRangeException.ThrowIfLessThan(total, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, total);
        if (unit == PercentUnit)
            throw new ArgumentException($"Use {nameof(Percent)} for a percentage.", nameof(unit));
        Unit = unit;
        Position = position;
        Total = total;
    }

    private FileProgress(int percent)
    {
        Unit = PercentUnit;
        Position = Math.Clamp(percent, 0, 100);
        Total = 100;
    }

    /// <summary>
    /// How much of the file is done, from 0 to 100 (clamped), for work that goes as one stream rather than in parts,
    /// such as encoding a video (docs/specs/media.md). <see cref="Position"/> is then the part done, not the one started.
    /// </summary>
    public static FileProgress Percent(int percent) => new(percent);

    public bool IsPercent => Unit == PercentUnit;

    public string Unit { get; }

    public int Position { get; }

    public int Total { get; }
}
