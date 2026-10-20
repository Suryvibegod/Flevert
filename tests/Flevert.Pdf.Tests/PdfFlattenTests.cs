using System.Text.Json;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/pdf.md, v0.2.2: <c>Flatten forms and comments</c>, success criterion 5.</summary>
public sealed class PdfFlattenTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static PdfFlatten Op() => new(Qpdf.NextToApp);

    [Fact]
    public void It_is_not_in_the_right_click_menu()
    {
        var op = Op();

        Assert.Equal(("pdf.flatten", ""), (op.Id, op.MenuPath));
        Assert.Equal([".pdf"], op.Extensions);
        Assert.Equal(4, op.MaxParallel);
    }

    [Fact]
    public async Task A_filled_form_keeps_its_answers_as_text_on_the_page_and_has_no_field_left_to_fill()
    {
        var input = _dir.Combine("Tờ khai.pdf");
        ToolPdf.WriteFilledForm(input, "Nguyen Van A");

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Tờ khai (flattened).pdf"), output);
        Assert.False(await HasForm(output));
        Assert.DoesNotContain("/Annots", await QdfText(output), StringComparison.Ordinal);
        using var pdf = PdfDocument.Open(output);
        // The answer is text: it can be selected and searched.
        Assert.Contains("Nguyen Van A", pdf.GetText(0), StringComparison.Ordinal);
        // The comment is drawn where it was: a red border from 100, 400 to 200, 500 (y up).
        var page = pdf.Render(0, 72);
        Assert.Equal((255, 0, 0, 255), Pixel(page, 101, 842 - 450));
        Assert.Equal((255, 255, 255, 255), Pixel(page, 150, 842 - 450));
        // The original keeps its form.
        Assert.True(await HasForm(input));
    }

    [Fact]
    public async Task A_form_that_asks_viewers_to_draw_its_fields_gets_them_drawn()
    {
        var input = _dir.Combine("form.pdf");
        ToolPdf.WriteFilledForm(input, "Tran Thi B", needAppearances: true);

        var output = await Op().RunAsync(input, CancellationToken.None);

        Assert.False(await HasForm(output));
        using var pdf = PdfDocument.Open(output);
        Assert.Contains("Tran Thi B", pdf.GetText(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pdf_without_forms_or_comments_is_skipped_and_nothing_is_written()
    {
        var input = _dir.Combine("plain.pdf");
        File.Copy(Fixture("three-colours.pdf"), input);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal("This PDF has no forms or comments to flatten.", ex.Message);
        Assert.Equal(["plain.pdf"], FileNames(_dir.Path));
    }

    // Whether qpdf finds an interactive form with fields (qpdf --json, "acroform").
    private static async Task<bool> HasForm(string path)
    {
        var run = await ExternalTool.RunAsync(Path.Combine(AppContext.BaseDirectory, "qpdf.exe"), ["--json=2", "--json-key=acroform", path], TimeSpan.FromSeconds(30));
        Assert.True(run.ExitCode == 0, run.StandardError);
        using var json = JsonDocument.Parse(run.StandardOutput);
        var form = json.RootElement.GetProperty("acroform");
        return form.GetProperty("hasacroform").GetBoolean() || form.GetProperty("fields").GetArrayLength() > 0;
    }
}
