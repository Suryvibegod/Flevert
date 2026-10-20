using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary>
/// <see cref="PdfTools"/>: the PDF tools, the contract the menu Flevert PDF tools codes against (docs/specs/pdf.md,
/// v0.2.2), and the operations each gives.
/// </summary>
public sealed class PdfToolsTests
{
    private static readonly Dictionary<string, string> NoOptions = [];

    private static readonly string[] WorkerTools = ["pdf.remove-blank-pages", "pdf.n-up-2", "pdf.fit-a4", "pdf.watermark", "pdf.page-numbers", "pdf.extract-images"];

    [Fact]
    public void The_tools_are_the_operations_of_v022()
    {
        Assert.Equal(
            ["pdf.remove-blank-pages", "pdf.n-up-2", "pdf.n-up-4", "pdf.fit-a4", "pdf.split-10mb", "pdf.split-25mb", "pdf.watermark",
                "pdf.page-numbers", "pdf.flatten", "pdf.extract-text", "pdf.extract-images"],
            PdfTools.All.Select(tool => tool.OperationId));
    }

    [Fact]
    public void Options_have_the_common_choice_as_default()
    {
        var numbers = PdfTools.All.Single(tool => tool.OperationId == "pdf.page-numbers").Options;
        Assert.Equal(
            [
                new PdfToolOption("position", PdfToolOptionKind.Choice, ["bottom-center", "bottom-right", "top-right"], "bottom-center", 0),
                new PdfToolOption("style", PdfToolOptionKind.Choice, ["1", "1 / N", "Page 1 of N"], "1", 0),
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
    public void Every_tool_but_extract_text_is_placed_once_in_flevert_pdf_tools_and_runs_by_its_id()
    {
        var placed = MenuOperations.All.Where(op => op is Core.Menu.IMenuPlacement { Cascade: var cascade } && cascade == Core.Menu.MenuCascade.PdfTools).ToList();

        Assert.Equal(PdfTools.All.Select(tool => tool.OperationId).Where(id => id != "pdf.extract-text").Order(StringComparer.Ordinal),
            placed.Select(op => op.Id).Order(StringComparer.Ordinal));
        // Extract text is Convert to > Text; flevert.exe's catalog (the menus' operations) has every id once.
        Assert.Contains(PdfRenderOperations.All, op => op.Id == "pdf.extract-text");
        _ = new OperationCatalog([.. MenuOperations.All]);
    }

    [Fact]
    public void Operations_that_run_a_worker_run_one_file_at_a_time_and_qpdf_ones_four()
    {
        int Parallel(string id) => PdfTools.Create(id, NoOptions).MaxParallel;

        Assert.Equal(4, Parallel("pdf.split-10mb"));
        Assert.Equal(4, Parallel("pdf.flatten"));
        Assert.All(WorkerTools, id => Assert.Equal(1, Parallel(id)));
    }

    private sealed class OptionComparer : IEqualityComparer<PdfToolOption>
    {
        public bool Equals(PdfToolOption? x, PdfToolOption? y) =>
            x is not null && y is not null && x with { Choices = [] } == y with { Choices = [] } && x.Choices.SequenceEqual(y.Choices);

        public int GetHashCode(PdfToolOption obj) => obj.Key.GetHashCode(StringComparison.Ordinal);
    }
}
