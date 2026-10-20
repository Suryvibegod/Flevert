namespace Flevert.Pdf.Tests;

/// <summary>The PDF tools window without the window (docs/specs/pdf.md, v0.2.2, "Cửa sổ PDF tools").</summary>
public sealed class PdfToolPickerTests : IDisposable
{
    private static readonly PdfTool Blank = new("pdf.remove-blank-pages", "Remove blank pages", "Drops empty pages.", PdfToolGroup.Pages, "document_dismiss", []);
    private static readonly PdfTool TwoUp = new("pdf.n-up-2", "2 pages per sheet", "Two pages on a sheet.", PdfToolGroup.Pages, "document_one_page_columns", []);
    private static readonly PdfTool Watermark = new("pdf.watermark", "Add watermark", "Writes text on every page.", PdfToolGroup.AddToPages, "draw_text",
        [new("text", "Watermark text", PdfToolOptionKind.Text, [], "", 100)]);
    private static readonly PdfTool Numbers = new("pdf.page-numbers", "Add page numbers", "Numbers every page.", PdfToolGroup.AddToPages, "document_page_bottom_center",
        [
            new("position", "Position", PdfToolOptionKind.Choice, ["Bottom center", "Bottom right", "Top right"], "Bottom center", 0),
            new("style", "Style", PdfToolOptionKind.Choice, ["1", "1 / 12", "Page 1 of 12"], "1", 0),
        ]);
    private static readonly PdfTool Text = new("pdf.extract-text", "Extract text", "Saves the text.", PdfToolGroup.Extract, "document_text_extract", []);

    // Given out of group order: the window groups them, keeping the order within each group.
    private static readonly PdfTool[] Tools = [Text, Blank, Watermark, TwoUp, Numbers];

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Tools_are_shown_by_group_pages_then_add_to_pages_then_extract_each_keeping_its_order()
    {
        var picker = new PdfToolPicker(Tools, null);

        Assert.Equal(["Pages", "Add to pages", "Extract"], picker.Sections.Select(s => s.Title));
        Assert.Equal([Blank, TwoUp], picker.Sections[0].Tools);
        Assert.Equal([Watermark, Numbers], picker.Sections[1].Tools);
        Assert.Equal([Text], picker.Sections[2].Tools);
        Assert.Equal([Blank, TwoUp, Watermark, Numbers, Text], picker.Tools);
    }

    [Fact]
    public void The_first_tool_is_selected_unless_one_was_chosen_last_time()
    {
        Assert.Equal(Blank, new PdfToolPicker(Tools, null).Selected);
        Assert.Equal(Numbers, new PdfToolPicker(Tools, "pdf.page-numbers").Selected);
        // A tool that no longer exists, or a damaged setting, is ignored.
        Assert.Equal(Blank, new PdfToolPicker(Tools, "pdf.gone").Selected);
    }

    [Fact]
    public void A_tool_without_options_runs_at_once_on_run()
    {
        var picker = new PdfToolPicker(Tools, null);

        Assert.Null(picker.Problem());
        var choice = picker.Answer();
        Assert.Equal(Blank, choice.Tool);
        Assert.Empty(choice.Options);
    }

    [Fact]
    public void Options_start_at_their_defaults_the_common_choice_so_run_needs_no_more()
    {
        var picker = new PdfToolPicker(Tools, "pdf.page-numbers");

        var choice = picker.Answer();

        Assert.Equal(new Dictionary<string, string> { ["position"] = "Bottom center", ["style"] = "1" }, choice.Options);
    }

    [Fact]
    public void A_choice_set_is_part_of_the_answer_and_one_not_offered_is_refused()
    {
        var picker = new PdfToolPicker(Tools, "pdf.page-numbers");

        picker.Set("style", "Page 1 of 12");
        Assert.Equal("Page 1 of 12", picker.Answer().Options["style"]);

        picker.Set("position", "Middle");
        Assert.Equal("Choose a position.", picker.Problem()!.Value.Message);
        Assert.Throws<ArgumentException>(() => picker.Set("colour", "red"));
    }

    [Fact]
    public void The_watermark_needs_its_text_before_run()
    {
        var picker = new PdfToolPicker(Tools, "pdf.watermark");

        Assert.Equal(("text", "Type the watermark text."), (picker.Problem()!.Value.Option.Key, picker.Problem()!.Value.Message));
        Assert.Throws<InvalidOperationException>(picker.Answer);
        picker.Set("text", "   ");
        Assert.NotNull(picker.Problem());
    }

    [Fact]
    public void The_watermark_text_is_vietnamese_friendly_trimmed_and_at_most_100_characters()
    {
        var picker = new PdfToolPicker(Tools, "pdf.watermark");
        const string Text = "Chỉ dùng để nộp hồ sơ vay vốn";

        picker.Set("text", "  " + Text + " ");
        Assert.Null(picker.Problem());
        Assert.Equal(Text, picker.Answer().Options["text"]);

        picker.Set("text", new string('ư', 100));
        Assert.Null(picker.Problem());
        picker.Set("text", new string('ư', 101));
        Assert.Equal("The watermark text can be at most 100 characters.", picker.Problem()!.Value.Message);
        picker.Set("text", "two\nlines");
        Assert.Equal("The watermark text must be one line of text.", picker.Problem()!.Value.Message);
    }

    [Fact]
    public void Going_to_another_tool_and_back_keeps_what_was_typed_and_each_tool_answers_with_its_own_options()
    {
        var picker = new PdfToolPicker(Tools, "pdf.watermark");
        picker.Set("text", "Bản sao");

        picker.Select("pdf.page-numbers");
        Assert.Equal(["position", "style"], picker.Answer().Options.Keys.Order());
        picker.Select("pdf.watermark");

        Assert.Equal("Bản sao", picker.Answer().Options["text"]);
    }

    [Theory]
    [InlineData("bottom-center", "Bottom center")]
    [InlineData("top-right", "Top right")]
    [InlineData("1 / N", "1 / N")]
    [InlineData("Page 1 of N", "Page 1 of N")]
    public void Choices_written_as_keys_read_as_words(string value, string shown)
    {
        Assert.Equal(shown, PdfToolPicker.ChoiceText(value));
    }

    [Theory]
    [InlineData(1, "1 PDF selected")]
    [InlineData(3, "3 PDFs selected")]
    [InlineData(100, "100 PDFs selected")]
    public void The_line_at_the_top_says_how_many_pdfs_are_selected(int files, string line)
    {
        Assert.Equal(line, PdfToolPicker.SelectedLine(files));
    }

    [Fact]
    public void Every_real_tool_has_a_name_a_one_line_description_an_icon_name_and_a_known_group()
    {
        foreach (var tool in PdfTools.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name), tool.OperationId);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.OperationId);
            Assert.DoesNotContain('\n', tool.Description);
            Assert.Matches("^[a-z0-9_]+$", tool.Icon);
            Assert.True(Enum.IsDefined(tool.Group), tool.OperationId);
            foreach (var option in tool.Options)
                Assert.True(option.Kind == PdfToolOptionKind.Text || option.Choices.Contains(option.Default), $"{tool.OperationId}: {option.Key}");
        }

        // Every real tool starts at a valid answer, except one that needs text typed in.
        foreach (var tool in PdfTools.All.Where(t => t.Options.All(o => o.Kind == PdfToolOptionKind.Choice)))
            Assert.Null(new PdfToolPicker(PdfTools.All, tool.OperationId).Problem());
    }

    [Fact]
    public void The_tool_chosen_last_is_remembered_by_its_id_alone()
    {
        var settings = new PdfToolSettings(Path.Combine(_dir.Path, "settings", "last-pdf-tool.txt"));
        Assert.Null(settings.LastToolId());

        settings.Remember("pdf.watermark");

        Assert.Equal("pdf.watermark", settings.LastToolId());
        Assert.Equal("pdf.watermark", File.ReadAllText(Path.Combine(_dir.Path, "settings", "last-pdf-tool.txt")));
    }

    [Theory]
    [InlineData("C:\\Users\\me\\secret.pdf")]
    [InlineData("pdf.watermark\nmore")]
    [InlineData("")]
    public void A_damaged_setting_means_none(string content)
    {
        var path = _dir.Combine("last-pdf-tool.txt");
        File.WriteAllText(path, content);

        Assert.Null(new PdfToolSettings(path).LastToolId());
    }
}
