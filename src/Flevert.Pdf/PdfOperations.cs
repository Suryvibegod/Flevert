using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// The PDF operations offered in the menu, in menu order (docs/specs/pdf.md, table of operations): Convert, Merge PDFs,
/// Split into pages, Rotate. Nothing here loads pdfium or starts qpdf.
/// </summary>
public static class PdfOperations
{
    public static IReadOnlyList<IMenuOperation> All { get; } =
    [
        .. PdfRenderOperations.All,
        new PdfMerge(Qpdf.NextToApp),
        new PdfSplit(Qpdf.NextToApp),
        new PdfRotate(90, Qpdf.NextToApp),
        new PdfRotate(270, Qpdf.NextToApp),
        new PdfRotate(180, Qpdf.NextToApp),
    ];
}
