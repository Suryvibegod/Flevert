using Flevert.Core;

// TEMPORARY stand-in for src/Flevert.Pdf/PdfTools.cs, which part B of v0.2.2 writes: the same API, so the PDF tools
// window and pdf.tools build and run before the tools exist. Delete this file when PdfTools.cs arrives.
namespace Flevert.Pdf;

public enum PdfToolGroup
{
    Pages,
    AddToPages,
    Extract,
}

public enum PdfToolOptionKind
{
    Choice,
    Text,
}

public sealed record PdfToolOption(string Key, string Label, PdfToolOptionKind Kind, IReadOnlyList<string> Choices, string Default, int MaxLength);

public sealed record PdfTool(string OperationId, string Name, string Description, PdfToolGroup Group, string Icon, IReadOnlyList<PdfToolOption> Options);

public static class PdfTools
{
    public static IReadOnlyList<PdfTool> All { get; } =
    [
        new("pdf.remove-blank-pages", "Remove blank pages", "Drops empty pages left by a two-sided scan.", PdfToolGroup.Pages, "document_dismiss", []),
        new("pdf.n-up-2", "2 pages per sheet", "Puts two pages side by side on each A4 sheet, to print on less paper.", PdfToolGroup.Pages, "document_one_page_columns", []),
        new("pdf.n-up-4", "4 pages per sheet", "Puts four pages on each A4 sheet.", PdfToolGroup.Pages, "grid", []),
        new("pdf.fit-a4", "Fit pages to A4", "Scales every page to A4, so it prints without cropping.", PdfToolGroup.Pages, "document_fit", []),
        new("pdf.split-10mb", "Split into parts under 10 MB", "Cuts a large PDF into parts an email will take.", PdfToolGroup.Pages, "arrow_split", []),
        new("pdf.split-25mb", "Split into parts under 25 MB", "Cuts a large PDF into parts a chat app will take.", PdfToolGroup.Pages, "arrow_split", []),
        new("pdf.watermark", "Add watermark", "Writes your text across every page. Anyone with an editor can still remove it.", PdfToolGroup.AddToPages, "draw_text",
            [new("text", "Watermark text", PdfToolOptionKind.Text, [], "", 100)]),
        new("pdf.page-numbers", "Add page numbers", "Numbers every page.", PdfToolGroup.AddToPages, "document_page_bottom_center",
            [
                new("position", "Position", PdfToolOptionKind.Choice, ["Bottom center", "Bottom right", "Top right"], "Bottom center", 0),
                new("style", "Style", PdfToolOptionKind.Choice, ["1", "1 / 12", "Page 1 of 12"], "1", 0),
            ]),
        new("pdf.flatten", "Flatten forms and comments", "Makes filled-in forms and comments part of the page, so they can no longer be changed.", PdfToolGroup.AddToPages, "layer", []),
        new("pdf.extract-text", "Extract text", "Saves the text of every page as a .txt file.", PdfToolGroup.Extract, "document_text_extract", []),
        new("pdf.extract-images", "Extract images", "Saves the pictures in the PDF as image files.", PdfToolGroup.Extract, "image_multiple", []),
    ];

    public static IOperation Create(string operationId, IReadOnlyDictionary<string, string> options, Qpdf qpdf)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(qpdf);
        return operationId == "pdf.extract-text"
            ? new PdfExtractText()
            : new NotYetBuilt(All.Single(tool => tool.OperationId == operationId).OperationId);
    }

    private sealed class NotYetBuilt(string id) : IOperation
    {
        public string Id => id;

        public string MenuPath => "";

        public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

        public Task<string> RunAsync(string inputPath, CancellationToken ct) =>
            throw new NotSupportedException($"{id} is not built yet (part B of v0.2.2).");
    }
}
