using Flevert.Core;

namespace Flevert.Pdf.Tests;

public sealed class QpdfTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Finds_qpdf_next_to_the_running_program()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "qpdf.exe"), Qpdf.NextToApp.ExePath);
        Assert.True(File.Exists(Qpdf.NextToApp.ExePath));
    }

    [Fact]
    public void The_timeout_is_two_minutes_plus_one_second_per_megabyte()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), Qpdf.TimeoutFor(0));
        Assert.Equal(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(100), Qpdf.TimeoutFor(100L * 1024 * 1024));
        Assert.Equal(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(2048), Qpdf.TimeoutFor(PdfDocument.MaxFileBytes));
    }

    [Theory]
    [InlineData(@"C:\Users\me\in.pdf", @"\\?\C:\Users\me\in.pdf")]
    [InlineData(@"\\?\C:\Users\me\in.pdf", @"\\?\C:\Users\me\in.pdf")]
    [InlineData(@"\\server\share\in.pdf", @"\\?\UNC\server\share\in.pdf")]
    [InlineData(@"\\.\C:\Users\me\in.pdf", @"\\?\C:\Users\me\in.pdf")]
    // \\?\ turns off Windows' normalisation, so it is done first, as .NET does when it opens the same path.
    [InlineData(@"C:\Users\me\..\you\.\in.pdf", @"\\?\C:\Users\you\in.pdf")]
    [InlineData(@"C:/Users/me/in.pdf", @"\\?\C:\Users\me\in.pdf")]
    public void Paths_reach_qpdf_in_the_extended_form_it_opens_at_any_length(string path, string extended)
    {
        Assert.Equal(extended, Qpdf.ToExtendedPath(path));
    }

    [Fact]
    public async Task A_missing_qpdf_says_to_reinstall()
    {
        var qpdf = new Qpdf(_dir.Combine("qpdf.exe"));
        var input = _dir.Combine("in.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => qpdf.TransformAsync(input, _dir.Combine("out.pdf"), ["--rotate=+90:1-z"], CancellationToken.None));

        Assert.Equal(qpdf.ExePath, ex.FileName);
        Assert.Contains("qpdf.exe is missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Reinstall Flevert", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-x.pdf", @"C:\out.pdf")]
    [InlineData("@options.txt", @"C:\out.pdf")]
    [InlineData(@"C:\in.pdf", "--empty")]
    [InlineData(@"C:\in.pdf", @"\out.pdf")]
    [InlineData("C:\\in.pdf\n-x.pdf", @"C:\out.pdf")]
    public async Task Paths_must_be_fully_qualified_single_lines_so_qpdf_never_reads_one_as_an_option(string input, string output)
    {
        // qpdf has no end-of-options marker: a path starting with - or @ would be an option or a file of options, and
        // the paths reach qpdf one per line through @-.
        await Assert.ThrowsAsync<ArgumentException>(
            () => Qpdf.NextToApp.TransformAsync(input, output, ["--rotate=+90:1-z"], CancellationToken.None));
    }

    [Theory]
    [InlineData("--pages=*.pdf")]
    [InlineData("--rotate=+90:1-?")]
    public async Task Options_with_wildcard_characters_are_refused_because_qpdf_would_expand_them(string option)
    {
        var input = _dir.Combine("in.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Qpdf.NextToApp.TransformAsync(input, _dir.Combine("out.pdf"), [option], CancellationToken.None));
        Assert.False(File.Exists(_dir.Combine("out.pdf")));
    }

    [Fact]
    public async Task An_option_naming_a_file_of_options_is_refused()
    {
        // qpdf reads "@file" as more arguments from that file, which would reach qpdf unchecked.
        var input = _dir.Combine("in.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);
        var options = _dir.File("options.txt", "--rotate=+90:1-z\n");

        await Assert.ThrowsAsync<ArgumentException>(
            () => Qpdf.NextToApp.TransformAsync(input, _dir.Combine("out.pdf"), ["@" + options], CancellationToken.None));
        Assert.False(File.Exists(_dir.Combine("out.pdf")));
    }

    [Fact]
    public void A_long_qpdf_message_is_cut_without_splitting_a_character()
    {
        // The last 2 000 UTF-16 units are kept; here they would begin with the second half of the emoji.
        var stderr = "x😀" + new string('e', 1_999);

        var detail = Qpdf.Detail(new ExternalToolResult(2, "", stderr, false, false));

        Assert.Equal("…" + new string('e', 1_999), detail);
    }

    [Fact]
    public async Task An_output_qpdf_cannot_create_is_reported_as_a_file_error()
    {
        var input = _dir.Combine("in.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);

        var ex = await Assert.ThrowsAsync<IOException>(() => Qpdf.NextToApp.TransformAsync(
            input, _dir.Combine(@"missing folder\out.pdf"), ["--rotate=+90:1-z"], CancellationToken.None));

        Assert.Contains("could not write", ex.Message, StringComparison.Ordinal);
        Assert.Contains("No such file or directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_refused_before_qpdf_reads_it()
    {
        var input = _dir.Combine("in.pdf");
        File.Copy(TestPdf.Fixture("three-colours.pdf"), input);
        var qpdf = new Qpdf(Qpdf.NextToApp.ExePath) { MaxInputBytes = new FileInfo(input).Length - 1 };

        var ex = await Assert.ThrowsAsync<PdfException>(
            () => qpdf.TransformAsync(input, _dir.Combine("out.pdf"), ["--rotate=+90:1-z"], CancellationToken.None));

        Assert.Equal(PdfError.TooLarge, ex.Error);
        Assert.False(File.Exists(_dir.Combine("out.pdf")));
    }
}
