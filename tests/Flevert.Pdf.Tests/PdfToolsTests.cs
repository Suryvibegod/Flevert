using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary>
/// <see cref="PdfTools"/>: the tools of the PDF tools window, the contract the window codes against (docs/specs/pdf.md,
/// v0.2.2), and the operations each gives.
/// </summary>
public sealed class PdfToolsTests
{
    private static readonly Dictionary<string, string> NoOptions = [];

    private static readonly string[] WorkerTools = ["pdf.remove-blank-pages", "pdf.n-up-2", "pdf.fit-a4", "pdf.watermark", "pdf.page-numbers", "pdf.extract-images"];

    [Fact]
    public void The_tools_are_in_window_order_by_group()
    {
        Assert.Equal(
            [
                ("pdf.remove-blank-pages", "Remove blank pages", PdfToolGroup.Pages),
                ("pdf.n-up-2", "2 pages per sheet", PdfToolGroup.Pages),
                ("pdf.n-up-4", "4 pages per sheet", PdfToolGroup.Pages),
                ("pdf.fit-a4", "Fit pages to A4", PdfToolGroup.Pages),
                ("pdf.split-10mb", "Split into parts under 10 MB", PdfToolGroup.Pages),
                ("pdf.split-25mb", "Split into parts under 25 MB", PdfToolGroup.Pages),
                ("pdf.watermark", "Add watermark", PdfToolGroup.AddToPages),
                ("pdf.page-numbers", "Add page numbers", PdfToolGroup.AddToPages),
                ("pdf.flatten", "Flatten forms and comments", PdfToolGroup.AddToPages),
                ("pdf.extract-text", "Extract text", PdfToolGroup.Extract),
                ("pdf.extract-images", "Extract images", PdfToolGroup.Extract),
            ],
            PdfTools.All.Select(tool => (tool.OperationId, tool.Name, tool.Group)));
    }

    [Fact]
    public void Every_tool_says_in_one_sentence_what_it_gives_and_has_an_icon()
    {
        Assert.All(PdfTools.All, tool =>
        {
            Assert.EndsWith(".", tool.Description, StringComparison.Ordinal);
            Assert.Matches("^[a-z0-9_]+$", tool.Icon);
        });
        // The window must say a watermark does not stop editing (docs/specs/pdf.md, "Add watermark").
        Assert.Contains("does not stop", PdfTools.All.Single(tool => tool.OperationId == "pdf.watermark").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_have_the_common_choice_as_default()
    {
        var numbers = PdfTools.All.Single(tool => tool.OperationId == "pdf.page-numbers").Options;
        Assert.Equal(
            [
                new PdfToolOption("position", "Position", PdfToolOptionKind.Choice, ["bottom-center", "bottom-right", "top-right"], "bottom-center", 0),
                new PdfToolOption("style", "Style", PdfToolOptionKind.Choice, ["1", "1 / N", "Page 1 of N"], "1", 0),
            ],
            numbers,
            new OptionComparer());
        var text = Assert.Single(PdfTools.All.Single(tool => tool.OperationId == "pdf.watermark").Options);
        Assert.Equal(("text", PdfToolOptionKind.Text, "", 100), (text.Key, text.Kind, text.Default, text.MaxLength));
        Assert.All(PdfTools.All.Where(tool => tool.OperationId is not ("pdf.page-numbers" or "pdf.watermark")), tool => Assert.Empty(tool.Options));
    }

    [Fact]
    public void Each_tool_creates_its_operation_with_the_defaults()
    {
        foreach (var tool in PdfTools.All)
        {
            var op = PdfTools.Create(tool.OperationId, NoOptions);

            Assert.Equal(tool.OperationId, op.Id);
            Assert.Equal([".pdf"], op.Extensions);
        }

        var numbers = Assert.IsType<PdfPageNumbers>(PdfTools.Create("pdf.page-numbers", NoOptions));
        Assert.Equal((PdfPageNumberPosition.BottomCenter, PdfPageNumberStyle.Number), (numbers.Position, numbers.Style));
        Assert.Equal("", Assert.IsType<PdfWatermark>(PdfTools.Create("pdf.watermark", NoOptions)).Text);
    }

    [Theory]
    [InlineData("bottom-right", "1 / N", PdfPageNumberPosition.BottomRight, PdfPageNumberStyle.OfTotal)]
    [InlineData("top-right", "Page 1 of N", PdfPageNumberPosition.TopRight, PdfPageNumberStyle.Words)]
    public void The_users_options_are_used(string position, string style, PdfPageNumberPosition expectedPosition, PdfPageNumberStyle expectedStyle)
    {
        var op = Assert.IsType<PdfPageNumbers>(PdfTools.Create("pdf.page-numbers", new Dictionary<string, string> { ["position"] = position, ["style"] = style }));

        Assert.Equal((expectedPosition, expectedStyle), (op.Position, op.Style));
        Assert.Equal("Chỉ dùng để nộp hồ sơ",
            Assert.IsType<PdfWatermark>(PdfTools.Create("pdf.watermark", new Dictionary<string, string> { ["text"] = " Chỉ dùng để nộp hồ sơ " })).Text);
    }

    public static TheoryData<string, Dictionary<string, string>> Invalid() => new()
    {
        { "pdf.rotate-right", [] }, // not a tool
        { "pdf.reverse", [] },
        { "pdf.page-numbers", new() { ["position"] = "middle" } },
        { "pdf.page-numbers", new() { ["style"] = "i, ii, iii" } },
        { "pdf.page-numbers", new() { ["colour"] = "red" } },
        { "pdf.flatten", new() { ["text"] = "x" } },
        { "pdf.watermark", new() { ["text"] = new string('a', 101) } },
        { "pdf.watermark", new() { ["text"] = "two\nlines" } },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_unknown_tool_or_option_is_refused(string operationId, Dictionary<string, string> options) =>
        Assert.Throws<ArgumentException>(() => PdfTools.Create(operationId, options));

    [Fact]
    public void Every_tool_but_extract_text_is_in_the_catalog_without_a_menu_path()
    {
        Assert.Equal(PdfTools.All.Select(tool => tool.OperationId).Where(id => id != "pdf.extract-text"), PdfTools.Operations.Select(op => op.Id));
        Assert.All(PdfTools.Operations, op => Assert.Equal("", op.MenuPath));
        // Extract text is there already, and the menu itself has none of the new tools.
        Assert.Contains(PdfRenderOperations.All, op => op.Id == "pdf.extract-text");
        Assert.DoesNotContain(MenuOperations.All, op => PdfTools.Operations.Any(tool => tool.Id == op.Id));
        _ = new OperationCatalog([.. MenuOperations.All, .. PdfTools.Operations]);
    }

    [Fact]
    public void Operations_that_run_a_worker_run_one_file_at_a_time_and_qpdf_ones_four()
    {
        var parallel = PdfTools.Operations.ToDictionary(op => op.Id, op => op.MaxParallel);

        Assert.Equal(4, parallel["pdf.split-10mb"]);
        Assert.Equal(4, parallel["pdf.flatten"]);
        Assert.All(WorkerTools, id => Assert.Equal(1, parallel[id]));
    }

    private sealed class OptionComparer : IEqualityComparer<PdfToolOption>
    {
        public bool Equals(PdfToolOption? x, PdfToolOption? y) =>
            x is not null && y is not null && x with { Choices = [] } == y with { Choices = [] } && x.Choices.SequenceEqual(y.Choices);

        public int GetHashCode(PdfToolOption obj) => obj.Key.GetHashCode(StringComparison.Ordinal);
    }
}
