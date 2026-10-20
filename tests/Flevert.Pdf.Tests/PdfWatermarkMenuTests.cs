using Flevert.Core;
using Flevert.Core.Menu;

namespace Flevert.Pdf.Tests;

/// <summary>
/// <c>Add watermark…</c> in Flevert PDF tools: the one entry of the PDF menus that asks something, the text, once for the
/// batch when the user agrees, like the password dialog (docs/specs/pdf.md, v0.2.2).
/// </summary>
public sealed class PdfWatermarkMenuTests
{
    [Fact]
    public void It_is_add_watermark_in_flevert_pdf_tools()
    {
        var op = PdfToolMenu.All.OfType<PdfWatermarkMenu>().Single();

        Assert.Equal(("pdf.watermark", "Add watermark…"), (op.Id, op.MenuPath));
        Assert.Equal(MenuCascade.PdfTools, op.Cascade);
        Assert.Equal([".pdf"], op.Extensions);
    }

    [Fact]
    public async Task Five_files_ask_once_when_the_text_is_for_all_of_them_and_each_gets_it()
    {
        var prompt = new FakePrompt(new WatermarkAnswer("Chỉ dùng để nộp hồ sơ", 5));
        var written = new List<string>();
        var op = new PdfWatermarkMenu(create: text => new FakeTool(text, written)).WithPrompt(prompt);

        await Task.WhenAll(Enumerable.Range(1, 5).Select(i => op.RunAsync($@"C:\scans\{i}.pdf", CancellationToken.None)));

        Assert.Equal(1, prompt.Asked);
        Assert.Equal(Enumerable.Repeat("Chỉ dùng để nộp hồ sơ", 5), written);
    }

    [Fact]
    public async Task A_text_for_one_file_is_asked_again_for_the_next()
    {
        var prompt = new FakePrompt(new WatermarkAnswer("Bản sao", 1));
        var op = new PdfWatermarkMenu(create: text => new FakeTool(text, [])).WithPrompt(prompt);

        await op.RunAsync(@"C:\a.pdf", CancellationToken.None);
        await op.RunAsync(@"C:\b.pdf", CancellationToken.None);

        Assert.Equal(2, prompt.Asked);
    }

    [Fact]
    public async Task Cancel_skips_the_file_and_cancel_for_all_skips_them_all_with_one_question()
    {
        var prompt = new FakePrompt(new WatermarkAnswer(null, 3));
        var made = false;
        var op = new PdfWatermarkMenu(create: _ =>
        {
            made = true;
            return new FakeTool("", []);
        }).WithPrompt(prompt);

        foreach (var file in new[] { @"C:\a.pdf", @"C:\b.pdf", @"C:\c.pdf" })
        {
            var skipped = await Assert.ThrowsAsync<OperationSkippedException>(() => op.RunAsync(file, CancellationToken.None));
            Assert.Equal(WatermarkPrompts.NoTextMessage, skipped.Message);
        }

        Assert.Equal(1, prompt.Asked);
        Assert.False(made);
    }

    [Fact]
    public async Task Without_a_window_it_fails_clearly_rather_than_wait()
    {
        // FLEVERT_HEADLESS: flevert.exe keeps the catalog's operation, which has no window to ask in.
        var op = PdfToolMenu.All.OfType<PdfWatermarkMenu>().Single();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => op.RunAsync(@"C:\a.pdf", CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WatermarkPrompts.NoWindowMessage, failure.Message);
    }

    [Theory]
    [InlineData("", WatermarkPrompts.EmptyMessage)]
    [InlineData("   ", WatermarkPrompts.EmptyMessage)]
    [InlineData("two\nlines", WatermarkPrompts.NotOneLineMessage)]
    [InlineData("Chỉ dùng để nộp hồ sơ vay vốn", null)]
    public void The_dialog_lets_through_one_line_of_text(string text, string? problem)
    {
        Assert.Equal(problem, WatermarkPrompts.Problem(text));
    }

    [Fact]
    public void The_text_holds_at_most_what_the_tool_takes_100_characters()
    {
        Assert.Equal(100, WatermarkPrompts.MaxLength);
        Assert.Null(WatermarkPrompts.Problem(new string('ư', 100)));
        Assert.Equal(WatermarkPrompts.TooLongMessage, WatermarkPrompts.Problem(new string('ư', 101)));
    }

    private sealed class FakePrompt(WatermarkAnswer answer) : IWatermarkPrompt
    {
        private int _asked;

        public int Asked => _asked;

        public async Task<WatermarkAnswer> AskAsync(WatermarkQuestion question, CancellationToken ct)
        {
            Interlocked.Increment(ref _asked);
            await Task.Yield();
            return answer;
        }
    }

    private sealed class FakeTool(string text, List<string> written) : IOperation
    {
        public string Id => "pdf.watermark";

        public string MenuPath => "";

        public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

        public Task<string> RunAsync(string inputPath, CancellationToken ct)
        {
            lock (written)
                written.Add(text);
            return Task.FromResult(Path.ChangeExtension(inputPath, null) + " (watermarked).pdf");
        }
    }
}
