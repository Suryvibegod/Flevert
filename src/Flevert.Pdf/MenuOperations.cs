using Flevert.Core;
using Flevert.Core.Menu;
using Flevert.Image;

namespace Flevert.Pdf;

/// <summary>
/// Every image and PDF operation, in menu order: what flevert.exe lists and installs, the PDF tools of the second PDF menu
/// (Flevert PDF tools) included. An image's menu reads Convert to, Compress, Remove hidden info, Combine into PDF
/// (docs/specs/pdf.md, v0.2.2, "Menu ảnh": removing GPS before sending a photo is an everyday task, joining photos into
/// a PDF less so).
/// </summary>
public static class MenuOperations
{
    public static IReadOnlyList<IMenuOperation> All { get; } = [.. ImageOperations.All, new ImagesToPdf(), .. PdfOperations.All, .. PdfToolMenu.All];

    /// <summary>
    /// The icon and group of each entry at the top of the menu (docs/specs/pdf.md, v0.2.2, "Icon"): a separator goes
    /// between groups (converting and arranging · protecting; in Flevert PDF tools: on the pages · paper and size ·
    /// extracting). Formats inside Convert to, and the choices inside Print layout and Split by size, have no icon.
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
        ["Remove blank pages"] = new("document_dismiss", 4),
        ["Add page numbers"] = new("document_page_number", 4),
        ["Add watermark…"] = new("draw_text", 4),
        ["Print layout"] = new("layout_column_two", 5),
        ["Fit pages to A4"] = new("page_fit", 5),
        ["Flatten forms and comments"] = new("layer", 5),
        ["Split by size"] = new("cut", 5),
        ["Extract images"] = new("image_multiple", 6),
    };
}
