namespace Flevert.Core;

/// <summary>
/// How far an operation is inside one file, shown under the batch line as "<see cref="Unit"/> <see cref="Position"/> of
/// <see cref="Total"/>", e.g. "page 37 of 200". See ADR-0007.
/// </summary>
public sealed record FileProgress
{
    /// <param name="unit">What is counted, as a singular English noun in lower case: "page".</param>
    /// <param name="position">The one being worked on, from 1 to <paramref name="total"/>.</param>
    /// <param name="total">How many the file has, at least 1.</param>
    public FileProgress(string unit, int position, int total)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentOutOfRangeException.ThrowIfLessThan(total, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, total);
        Unit = unit;
        Position = position;
        Total = total;
    }

    public string Unit { get; }

    public int Position { get; }

    public int Total { get; }
}
