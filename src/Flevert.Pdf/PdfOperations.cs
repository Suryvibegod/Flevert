using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// The PDF operations of the menu, in menu order (docs/specs/pdf.md, v0.2.2, "Menu PDF"): Compress (Smaller, same
/// quality; Smallest, for sharing), Merge PDFs, Split into pages, Convert to (JPG images, PNG images), Rotate, then Protect
/// with password, Remove password, Remove hidden info, then More PDF tools…: 16 entries, all Explorer shows. Extract text
/// is here too, out of the menu (the PDF tools window runs it). Nothing here loads pdfium or starts qpdf. The password
/// operations and More PDF tools… here have no window to ask in (they fail clearly); flevert.exe gives the one it runs its
/// window (<see cref="IPasswordOperation.WithPrompt"/>, <see cref="PdfToolsOperation.WithPrompt"/>).
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
        new PdfProtect(Qpdf.NextToApp),
        new PdfUnlock(Qpdf.NextToApp),
        new PdfStripMetadata(Qpdf.NextToApp),
        new PdfToolsOperation(PdfToolCatalog.Create),
    ];
}

/// <summary>The tools of the PDF tools window as operations: what <c>More PDF tools…</c> runs, and <c>flevert.exe run</c>.</summary>
public static class PdfToolCatalog
{
    /// <summary>The operation of a tool with the options chosen.</summary>
    public static IOperation Create(PdfToolChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return PdfTools.Create(choice.Tool.OperationId, choice.Options, Qpdf.NextToApp);
    }

    /// <summary>
    /// Every tool with its default options, except those <paramref name="present"/> already holds: run by
    /// <c>flevert.exe run &lt;id&gt;</c>, without a window (docs/specs/pdf.md, v0.2.2). A tool that needs text typed in
    /// (the watermark) fails clearly then.
    /// </summary>
    public static IEnumerable<IOperation> WithDefaults(IEnumerable<IMenuOperation> present)
    {
        var ids = present.Select(op => op.Id).ToHashSet(StringComparer.Ordinal);
        return PdfTools.All
            .Where(tool => !ids.Contains(tool.OperationId))
            .Select(tool => Create(new PdfToolChoice(tool, tool.Options.ToDictionary(o => o.Key, o => o.Default, StringComparer.Ordinal))));
    }
}
