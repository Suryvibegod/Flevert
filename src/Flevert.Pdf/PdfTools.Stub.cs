using Flevert.Core;

// TEMPORARY stand-in for src/Flevert.Pdf/PdfTools.cs of part B of v0.2.2 (branch v022-b, commit 581ce32): the same
// public API and tool list, so the PDF tools menu builds and runs before the tools are merged. Only Extract text runs;
// the other tools fail with "not built yet". Delete this file when PdfTools.cs arrives.
namespace Flevert.Pdf;

/// <summary>The headed groups of the PDF tools window (docs/specs/pdf.md, v0.2.2), in window order.</summary>
public enum PdfToolGroup { Pages, AddToPages, Extract }

/// <summary>What the user gives an option: one of its choices, or text.</summary>
public enum PdfToolOptionKind { Choice, Text }

/// <summary>
/// An option of a tool: its key in <see cref="PdfTools.Create"/>'s options, the label the window shows, its kind, the
/// values it may take (<see cref="PdfToolOptionKind.Choice"/>; empty for text), the value it has when not given, and
/// the most characters it holds (<see cref="PdfToolOptionKind.Text"/>; 0 for a choice).
/// </summary>
public sealed record PdfToolOption(string Key, string Label, PdfToolOptionKind Kind, IReadOnlyList<string> Choices, string Default, int MaxLength);

// Icon: a Fluent UI System Icons name such as "document_page_number"; A maps names to .ico files.
/// <summary>A tool of the PDF tools window: its operation, short name, one sentence saying what it gives, group, icon and options.</summary>
public sealed record PdfTool(string OperationId, string Name, string Description, PdfToolGroup Group, string Icon, IReadOnlyList<PdfToolOption> Options);

/// <summary>
/// The tools of the "Flevert — PDF tools" window (docs/specs/pdf.md, v0.2.2), and the operation each runs with the options
/// the user chose. Every tool also runs headless with its defaults (<c>flevert.exe run &lt;op-id&gt; &lt;file&gt;</c>):
/// <see cref="Operations"/> are in flevert.exe's catalog, without a menu path.
/// </summary>
public static class PdfTools
{
    /// <summary>Option keys and values of Add page numbers.</summary>
    public const string Position = "position";
    public const string Style = "style";
    public const string BottomCenter = "bottom-center";
    public const string BottomRight = "bottom-right";
    public const string TopRight = "top-right";
    public const string NumberStyle = "1";
    public const string OfTotalStyle = "1 / N";
    public const string WordsStyle = "Page 1 of N";

    /// <summary>Option key of Add watermark: the text, required, at most 100 characters.</summary>
    public const string Text = "text";

    private static readonly IReadOnlyList<string> Positions = [BottomCenter, BottomRight, TopRight];
    private static readonly IReadOnlyList<string> Styles = [NumberStyle, OfTotalStyle, WordsStyle];

    /// <summary>
    /// In window order: Pages (Remove blank pages, 2 pages per sheet, 4 pages per sheet, Fit pages to A4, Split into parts
    /// under 10 MB, … 25 MB), AddToPages (Add watermark, Add page numbers, Flatten forms and comments), Extract (Extract
    /// text, Extract images).
    /// </summary>
    public static IReadOnlyList<PdfTool> All { get; } =
    [
        new("pdf.remove-blank-pages", "Remove blank pages", "Drops the empty pages a two-sided scan leaves.", PdfToolGroup.Pages, "document_dismiss", []),
        new("pdf.n-up-2", "2 pages per sheet", "Puts two pages side by side on each A4 sheet, to print on less paper.", PdfToolGroup.Pages, "layout_column_two", []),
        new("pdf.n-up-4", "4 pages per sheet", "Puts four pages on each A4 sheet, to print on less paper.", PdfToolGroup.Pages, "grid", []),
        new("pdf.fit-a4", "Fit pages to A4", "Scales every page to A4, so Letter, A3 and odd sizes print without being cut off.", PdfToolGroup.Pages, "page_fit", []),
        new("pdf.split-10mb", "Split into parts under 10 MB", "Splits the PDF into parts small enough to attach to an email.", PdfToolGroup.Pages, "cut", []),
        new("pdf.split-25mb", "Split into parts under 25 MB", "Splits the PDF into parts for apps that take files up to 25 MB.", PdfToolGroup.Pages, "cut", []),
        new("pdf.watermark", "Add watermark", "Writes your text across every page to mark what a copy is for. It does not stop anyone from editing the PDF.",
            PdfToolGroup.AddToPages, "stamp",
            [new(Text, "Text", PdfToolOptionKind.Text, [], "", 100)]),
        new("pdf.page-numbers", "Add page numbers", "Numbers every page, at the bottom or at the top right.", PdfToolGroup.AddToPages, "document_page_number",
            [
                new(Position, "Position", PdfToolOptionKind.Choice, Positions, BottomCenter, 0),
                new(Style, "Style", PdfToolOptionKind.Choice, Styles, NumberStyle, 0),
            ]),
        new("pdf.flatten", "Flatten forms and comments", "Makes filled forms and comments part of the page, so they can no longer be changed.",
            PdfToolGroup.AddToPages, "layer", []),
        new("pdf.extract-text", "Extract text", "Saves the text of every page as a .txt file.", PdfToolGroup.Extract, "document_text", []),
        new("pdf.extract-images", "Extract images", "Saves every picture in the PDF to a folder, photos exactly as they are in it.", PdfToolGroup.Extract, "image_multiple", []),
    ];

    /// <summary>
    /// Every tool's operation with its default options but Extract text, which the catalog has already
    /// (<see cref="PdfRenderOperations"/>): what flevert.exe adds to its catalog so <c>run &lt;op-id&gt;</c> works headless.
    /// None has a menu path. Add watermark has no text: run like this, it fails each file and says why.
    /// </summary>
    public static IReadOnlyList<IOperation> Operations { get; } =
        [.. All.Where(tool => tool.OperationId != "pdf.extract-text").Select(tool => Create(tool.OperationId, new Dictionary<string, string>()))];

    public static IOperation Create(string operationId, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var tool = All.FirstOrDefault(tool => tool.OperationId == operationId)
            ?? throw new ArgumentException($"'{operationId}' is not a PDF tool.", nameof(operationId));
        return tool.OperationId == "pdf.extract-text" ? new PdfExtractText() : new NotYetBuilt(tool.OperationId);
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
