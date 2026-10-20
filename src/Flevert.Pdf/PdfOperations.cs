using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// The PDF operations offered in the menu, in menu order (docs/specs/pdf.md, table of operations): Convert (JPG pages,
/// PNG pages, Text), Merge PDFs, Split into pages, Rotate, Compress (Lossless, For sharing), Remove metadata, Protect with
/// password, Remove password: 16 entries, all Explorer shows. Nothing here loads pdfium
/// or starts qpdf. The two password operations here have no window to ask in (they fail clearly); flevert.exe gives the
/// one it runs its dialog (<see cref="IPasswordOperation.WithPrompt"/>).
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
        new PdfCompress(Qpdf.NextToApp),
        new PdfCompressSharing(Qpdf.NextToApp),
        new PdfStripMetadata(Qpdf.NextToApp),
        new PdfProtect(Qpdf.NextToApp),
        new PdfUnlock(Qpdf.NextToApp),
    ];
}
