using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// The PDF operations of the Flevert menu, in menu order (docs/specs/pdf.md, v0.2.2, "Menu PDF"): Compress (Smaller, same
/// quality; Smallest, for sharing), Merge PDFs, Split into pages, Convert to (JPG images, PNG images, Text), Rotate, then
/// Protect with password, Remove password, Remove hidden info: 16 entries, all Explorer shows. The PDF tools are in a
/// second menu (<see cref="PdfToolMenu"/>). Nothing here loads pdfium or starts qpdf. The password operations here have
/// no window to ask in (they fail clearly); flevert.exe gives the one it runs its dialog
/// (<see cref="IPasswordOperation.WithPrompt"/>).
/// </summary>
public static class PdfOperations
{
    public static IReadOnlyList<IMenuOperation> All { get; } =
    [
        new PdfCompress(Qpdf.NextToApp),
        new PdfCompressSharing(Qpdf.NextToApp),
        new PdfMerge(Qpdf.NextToApp),
        new PdfSplit(Qpdf.NextToApp),
        .. PdfRenderOperations.All,
        new PdfRotate(90, Qpdf.NextToApp),
        new PdfRotate(270, Qpdf.NextToApp),
        new PdfRotate(180, Qpdf.NextToApp),
        new PdfWatermarkMenu(),
        new PdfProtect(Qpdf.NextToApp),
        new PdfUnlock(Qpdf.NextToApp),
        new PdfStripMetadata(Qpdf.NextToApp),
    ];
}
