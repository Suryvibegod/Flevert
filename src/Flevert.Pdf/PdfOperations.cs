using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>The PDF operations offered in the menu, in menu order. Nothing here loads pdfium or starts qpdf.</summary>
public static class PdfOperations
{
    public static IReadOnlyList<IOperation> All { get; } =
    [
        .. PdfRenderOperations.All,
        new PdfRotate(90, Qpdf.NextToApp),
        new PdfRotate(270, Qpdf.NextToApp),
        new PdfRotate(180, Qpdf.NextToApp),
    ];
}
