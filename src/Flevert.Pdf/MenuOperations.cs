using Flevert.Core;
using Flevert.Image;

namespace Flevert.Pdf;

/// <summary>
/// Every image and PDF operation, in menu order: what flevert.exe lists and installs. The menu shows operations in the
/// order given, so <see cref="ImagesToPdf"/> goes before the image menu's <c>Remove metadata</c>: an image's menu reads
/// Convert, Compress, Combine into PDF, Remove metadata (docs/specs/pdf.md, "Thay đổi ở core" item 7).
/// </summary>
public static class MenuOperations
{
    private const string RemoveMetadata = "Remove metadata";

    public static IReadOnlyList<IMenuOperation> All { get; } = InMenuOrder(ImageOperations.All, new ImagesToPdf(), PdfOperations.All);

    internal static IReadOnlyList<IMenuOperation> InMenuOrder(
        IReadOnlyList<IMenuOperation> imageOperations, IMenuOperation combine, IReadOnlyList<IMenuOperation> pdfOperations)
    {
        var operations = new List<IMenuOperation>(imageOperations);
        var at = operations.FindIndex(op => op.MenuPath == RemoveMetadata);
        operations.Insert(at < 0 ? operations.Count : at, combine);
        operations.AddRange(pdfOperations);
        return operations;
    }
}
