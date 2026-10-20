using System.Globalization;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Rotate/…</c>: turns every page by the same angle from wherever it was (a page already turned keeps its relative
/// turn), and writes <c>&lt;name&gt; (rotated).pdf</c> beside the original. qpdf only changes each page's /Rotate,
/// so the content itself is untouched.
/// </summary>
public sealed class PdfRotate : IOperation
{
    private readonly int _degrees;
    private readonly Qpdf _qpdf;

    /// <param name="degreesClockwise">90, 180 or 270.</param>
    public PdfRotate(int degreesClockwise, Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        (Id, MenuPath) = degreesClockwise switch
        {
            90 => ("pdf.rotate-right", "Rotate/90° right"),
            270 => ("pdf.rotate-left", "Rotate/90° left"),
            180 => ("pdf.rotate-180", "Rotate/180°"),
            _ => throw new ArgumentOutOfRangeException(nameof(degreesClockwise), degreesClockwise, "Use 90, 180 or 270."),
        };
        _degrees = degreesClockwise;
        _qpdf = qpdf;
    }

    public string Id { get; }

    public string MenuPath { get; }

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // Each file is its own qpdf process on one core, and the work is mostly reading and rewriting the file, so beyond a
    // few at once the disk is the limit. qpdf's memory grows with the PDF and is outside MemoryBudget; each qpdf is capped
    // by Qpdf.MaxMemoryFor (1 GB for a PDF under about 300 MB), so four ordinary PDFs at once stay within 4 GB.
    public int MaxParallel => 4;

    public Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        var desired = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (rotated).pdf");
        // "+" turns relative to each page's current /Rotate; "1-z" is every page.
        var rotate = string.Create(CultureInfo.InvariantCulture, $"--rotate=+{_degrees}:1-z");
        return OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _qpdf.TransformAsync(inputPath, temp, [rotate], token), ct);
    }
}
