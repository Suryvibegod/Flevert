using Flevert.Core;
using Flevert.Core.Menu;
using Flevert.Image;

namespace Flevert.Pdf;

/// <summary>
/// Every image and PDF operation, in menu order: what flevert.exe lists and installs, with the PDF tools the window runs
/// (not in the menu). An image's menu reads Convert to, Compress, Remove hidden info, Combine into PDF (docs/specs/pdf.md,
/// v0.2.2, "Menu ảnh": removing GPS before sending a photo is an everyday task, joining photos into a PDF less so).
/// </summary>
public static class MenuOperations
{
    public static IReadOnlyList<IMenuOperation> All { get; } = Everything();

    /// <summary>
    /// The icon and group of each entry at the top of the menu (docs/specs/pdf.md, v0.2.2, "Icon"): a separator goes
    /// between groups (converting and arranging · protecting · more). Formats inside Convert to have no icon.
    /// </summary>
    public static IReadOnlyDictionary<string, MenuEntryLook> Looks { get; } = new Dictionary<string, MenuEntryLook>(StringComparer.Ordinal)
    {
        ["Convert to"] = new("arrow_swap", 1),
        ["Compress"] = new("arrow_minimize", 1),
        ["Merge PDFs"] = new("merge", 1),
        ["Split into pages"] = new("split_vertical", 1),
        ["Rotate"] = new("arrow_rotate_clockwise", 1),
        ["Protect with password"] = new("lock_closed", 2),
        ["Remove password"] = new("lock_open", 2),
        ["Remove hidden info"] = new("shield", 2),
        ["Combine into PDF"] = new("document_pdf", 2),
        ["More PDF tools…"] = new("toolbox", 3),
    };

    private static List<IMenuOperation> Everything()
    {
        List<IMenuOperation> operations = [.. ImageOperations.All, new ImagesToPdf(), .. PdfOperations.All];
        operations.AddRange(PdfToolCatalog.WithDefaults(operations));
        return operations;
    }
}
