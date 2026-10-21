using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary><see cref="Qpdf.SelectPagesAsync"/>: some pages of a PDF, through qpdf's <c>@-</c> like every path.</summary>
public sealed class QpdfSelectPagesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("1", true)]
    [InlineData("1-3,5,7-9", true)]
    [InlineData("2000", true)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1,", false)]
    [InlineData("z-1", false)]
    [InlineData("1 - 3", false)]
    [InlineData("-1", false)]
    [InlineData("--pages", false)]
    [InlineData(@"C:\a.pdf", false)]
    [InlineData("1\n2", false)]
    [InlineData("1\n", false)]
    public void Only_numbers_and_ranges_separated_by_commas_are_a_list_of_pages(string text, bool expected) =>
        Assert.Equal(expected, Qpdf.IsPageRange(text));

    [Fact]
    public async Task Keeping_the_document_keeps_what_it_carries_and_drops_the_other_pages()
    {
        var input = _dir.Combine("trang.pdf");
        WriteWidths(input, 101, 102, 103, 104, 105);
        var withAttachment = _dir.Combine("with info.pdf");
        await Flevert.Core.ExternalTool.RunAsync(Qpdf.NextToApp.ExePath,
            ["--add-attachment", input, "--", input, withAttachment], TimeSpan.FromSeconds(30));
        var output = _dir.Combine("out.pdf");

        await Qpdf.NextToApp.SelectPagesAsync(withAttachment, "1,3-4", output, keepDocument: true, CancellationToken.None);

        Assert.Equal([101, 103, 104], PageWidths(output));
        Assert.Contains("/EmbeddedFiles", await QdfText(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_pdf_of_some_pages_carries_nothing_else()
    {
        var input = _dir.Combine("trang.pdf");
        WriteWidths(input, 101, 102, 103);
        var withAttachment = _dir.Combine("attached.pdf");
        await Flevert.Core.ExternalTool.RunAsync(Qpdf.NextToApp.ExePath,
            ["--add-attachment", input, "--", input, withAttachment], TimeSpan.FromSeconds(30));
        var output = _dir.Combine("out.pdf");

        await Qpdf.NextToApp.SelectPagesAsync(withAttachment, "2-3", output, keepDocument: false, CancellationToken.None);

        Assert.Equal([102, 103], PageWidths(output));
        Assert.DoesNotContain("/EmbeddedFiles", await QdfText(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anything_but_a_list_of_pages_is_refused_before_qpdf_runs()
    {
        var input = _dir.Combine("trang.pdf");
        WriteWidths(input, 101);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Qpdf.NextToApp.SelectPagesAsync(input, "1 --encrypt", _dir.Combine("out.pdf"), keepDocument: true, CancellationToken.None));
    }
}
