using System.Text.Json;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>docs/specs/pdf.md, v0.2.2: <c>Lock filled forms</c>, success criterion 5.</summary>
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
    public async Task A_vietnamese_answer_with_no_appearance_fails_rather_than_turning_into_question_marks()
    {
        // qpdf draws a field only in its font's single-byte encoding: "Nguy?n V?n ?" is what it would write.
        var input = _dir.Combine("Tờ khai.pdf");
        ToolPdf.WriteFilledForm(input, "Nguyễn Văn Ă", needAppearances: true);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.CannotRedrawForm, ex.Error);
        Assert.StartsWith("This form has answers Flevert cannot redraw", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["Tờ khai.pdf"], FileNames(_dir.Path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Drawn by the viewer")]
    public async Task A_vietnamese_answer_in_a_form_that_asks_to_be_drawn_fails_even_with_a_drawing(string drawn)
    {
        // pypdf and pdftk fill a form by setting the answer and /NeedAppearances, and keep the blank form's drawing: locked
        // as drawn, the answer would be gone. Flevert cannot tell that drawing from one a viewer made of the answer.
        var input = _dir.Combine("Tờ khai.pdf");
        ToolPdf.WriteFilledForm(input, "Nguyễn Văn A", needAppearances: true, drawn: drawn);

        var ex = await Assert.ThrowsAsync<PdfException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.CannotRedrawForm, ex.Error);
        Assert.Equal(["Tờ khai.pdf"], FileNames(_dir.Path));
    }

    [Fact]
    public void Answers_qpdf_can_and_cannot_draw_are_told_apart()
    {
        Assert.True(PdfFlatten.CanRedraw("Nguyen Van A, 12/3"));
        Assert.False(PdfFlatten.CanRedraw("Nguyễn"));
        Assert.False(PdfFlatten.CanRedraw("José"));
        Assert.False(PdfFlatten.CanRedraw("line\nbreak"));
    }

    [Fact]
    public async Task A_pdf_without_forms_or_comments_is_skipped_and_nothing_is_written()
    {
        var input = _dir.Combine("plain.pdf");
        File.Copy(Fixture("three-colours.pdf"), input);

        var ex = await Assert.ThrowsAsync<OperationSkippedException>(() => Op().RunAsync(input, CancellationToken.None));

        Assert.Equal("This PDF has no filled forms or comments to lock.", ex.Message);
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
