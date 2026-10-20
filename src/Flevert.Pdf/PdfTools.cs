using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>What the user gives an option: one of its choices, or text.</summary>
public enum PdfToolOptionKind { Choice, Text }

/// <summary>
/// An option of a tool: its key in <see cref="PdfTools.Create"/>'s options, its kind, the values it may take
/// (<see cref="PdfToolOptionKind.Choice"/>; empty for text), the value it has when not given, and the most characters it
/// holds (<see cref="PdfToolOptionKind.Text"/>, as people count them; 0 for a choice).
/// </summary>
public sealed record PdfToolOption(string Key, PdfToolOptionKind Kind, IReadOnlyList<string> Choices, string Default, int MaxLength);

/// <summary>A PDF tool: its operation and its options. The menu names and places it (<see cref="PdfToolMenu"/>).</summary>
public sealed record PdfTool(string OperationId, IReadOnlyList<PdfToolOption> Options);

/// <summary>
/// The PDF tools of v0.2.2 (docs/specs/pdf.md), and the operation each runs with the options given. The second PDF menu,
/// Flevert: more PDF tools, places them (<see cref="PdfToolMenu"/>), and flevert.exe runs them by id from there
/// (<c>flevert.exe run &lt;op-id&gt; &lt;file&gt;</c>); Extract text is Convert to &gt; Text of the Flevert menu.
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

    /// <summary>Option key of Add watermark: the text, required, at most <see cref="PdfEdit.MaxWatermarkLength"/> characters.</summary>
    public const string Text = "text";

    private static readonly IReadOnlyList<string> Positions = [BottomCenter, BottomRight, TopRight];
    private static readonly IReadOnlyList<string> Styles = [NumberStyle, OfTotalStyle, WordsStyle];

    /// <summary>Every tool, by operation id.</summary>
    public static IReadOnlyList<PdfTool> All { get; } =
    [
        new("pdf.remove-blank-pages", []),
        new("pdf.n-up-2", []),
        new("pdf.n-up-4", []),
        new("pdf.fit-a4", []),
        new("pdf.split-10mb", []),
        new("pdf.split-25mb", []),
        new("pdf.watermark", [new(Text, PdfToolOptionKind.Text, [], "", PdfEdit.MaxWatermarkLength)]),
        new("pdf.page-numbers",
            [
                new(Position, PdfToolOptionKind.Choice, Positions, BottomCenter, 0),
                new(Style, PdfToolOptionKind.Choice, Styles, NumberStyle, 0),
            ]),
        new("pdf.flatten", []),
        new("pdf.extract-text", []),
        new("pdf.extract-images", []),
    ];

    /// <summary>
    /// The operation for a tool with the user's options (missing keys → their default), reading PDFs with the flevert.exe
    /// and qpdf.exe beside the running program, as <see cref="PdfOperations"/> does.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="operationId"/> is not a tool's, an option's key is not the tool's, a choice is not one of its
    /// <see cref="PdfToolOption.Choices"/>, or a text is too long or not one line of characters.
    /// </exception>
    public static IOperation Create(string operationId, IReadOnlyDictionary<string, string> options) =>
        Create(operationId, options, Qpdf.NextToApp, PdfWorker.NextToApp);

    /// <summary>As <see cref="Create(string, IReadOnlyDictionary{string, string})"/>, with the qpdf and worker tests built.</summary>
    internal static IOperation Create(string operationId, IReadOnlyDictionary<string, string> options, Qpdf qpdf, PdfWorker worker)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        ArgumentNullException.ThrowIfNull(options);
        var tool = All.FirstOrDefault(tool => tool.OperationId == operationId)
            ?? throw new ArgumentException($"'{operationId}' is not a PDF tool.", nameof(operationId));
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in tool.Options)
            values[option.Key] = option.Default;
        foreach (var (key, value) in options)
        {
            var option = tool.Options.FirstOrDefault(option => option.Key == key)
                ?? throw new ArgumentException($"'{operationId}' has no option '{key}'.", nameof(options));
            ArgumentNullException.ThrowIfNull(value, nameof(options));
            if (option.Kind == PdfToolOptionKind.Choice && !option.Choices.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException($"'{value}' is not a choice of '{key}'.", nameof(options));
            values[key] = value;
        }

        return operationId switch
        {
            "pdf.remove-blank-pages" => new PdfRemoveBlankPages(qpdf, worker),
            "pdf.n-up-2" => new PdfNUp(2, qpdf, worker),
            "pdf.n-up-4" => new PdfNUp(4, qpdf, worker),
            "pdf.fit-a4" => new PdfFitA4(qpdf, worker),
            "pdf.split-10mb" => new PdfSplitBySize(10, qpdf),
            "pdf.split-25mb" => new PdfSplitBySize(25, qpdf),
            "pdf.watermark" => new PdfWatermark(values[Text], qpdf, worker),
            "pdf.page-numbers" => new PdfPageNumbers(
                values[Position] switch
                {
                    BottomRight => PdfPageNumberPosition.BottomRight,
                    TopRight => PdfPageNumberPosition.TopRight,
                    _ => PdfPageNumberPosition.BottomCenter,
                },
                values[Style] switch
                {
                    OfTotalStyle => PdfPageNumberStyle.OfTotal,
                    WordsStyle => PdfPageNumberStyle.Words,
                    _ => PdfPageNumberStyle.Number,
                },
                qpdf, worker),
            "pdf.flatten" => new PdfFlatten(qpdf),
            "pdf.extract-text" => new PdfExtractText(worker),
            "pdf.extract-images" => new PdfExtractImages(worker),
            _ => throw new ArgumentException($"'{operationId}' is not a PDF tool.", nameof(operationId)),
        };
    }
}
