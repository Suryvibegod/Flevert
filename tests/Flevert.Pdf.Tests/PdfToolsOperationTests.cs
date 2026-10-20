using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary><c>More PDF tools…</c>: one question for the whole batch, then the chosen tool on every file.</summary>
public sealed class PdfToolsOperationTests
{
    private static readonly PdfTool Numbers = new("pdf.page-numbers", "Add page numbers", "Numbers every page.", PdfToolGroup.AddToPages, "document_page_bottom_center",
        [new("style", "Style", PdfToolOptionKind.Choice, ["1", "1 / 12"], "1", 0)]);

    [Fact]
    public void It_is_more_pdf_tools_at_the_end_of_the_pdf_menu()
    {
        var op = PdfOperations.All.OfType<PdfToolsOperation>().Single();

        Assert.Equal(("pdf.tools", "More PDF tools…"), (op.Id, op.MenuPath));
        Assert.Equal([".pdf"], op.Extensions);
        Assert.Same(op, PdfOperations.All[^1]);
    }

    [Fact]
    public async Task Five_files_ask_once_and_each_runs_the_tool_chosen_with_its_options()
    {
        var prompt = new FakePrompt(new PdfToolChoice(Numbers, new Dictionary<string, string> { ["style"] = "1 / 12" }));
        var created = new List<PdfToolChoice>();
        var tool = new RecordingTool();
        using var op = new PdfToolsOperation(choice =>
        {
            created.Add(choice);
            return tool;
        }).WithPrompt(prompt);

        var outputs = await Task.WhenAll(Enumerable.Range(1, 5).Select(i => op.RunAsync($@"C:\scans\{i}.pdf", CancellationToken.None)));

        Assert.Equal(1, prompt.Asked);
        Assert.Equal("1 / 12", Assert.Single(created).Options["style"]);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => $@"C:\scans\{i} (numbered).pdf"), outputs);
        Assert.Equal(5, tool.Ran.Count);
    }

    [Fact]
    public async Task Cancel_skips_every_file_and_makes_no_tool()
    {
        var prompt = new FakePrompt(null);
        var made = false;
        using var op = new PdfToolsOperation(_ =>
        {
            made = true;
            return new RecordingTool();
        }).WithPrompt(prompt);

        foreach (var file in new[] { @"C:\a.pdf", @"C:\b.pdf" })
        {
            var skipped = await Assert.ThrowsAsync<OperationSkippedException>(() => op.RunAsync(file, CancellationToken.None));
            Assert.Equal(PdfToolPrompts.CancelledMessage, skipped.Message);
        }

        Assert.Equal(1, prompt.Asked);
        Assert.False(made);
    }

    [Fact]
    public async Task Without_a_window_it_fails_clearly_rather_than_wait()
    {
        // FLEVERT_HEADLESS: flevert.exe keeps the catalog's operation, which has no window to ask in.
        var op = PdfOperations.All.OfType<PdfToolsOperation>().Single();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => op.RunAsync(@"C:\a.pdf", CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PdfToolPrompts.NoWindowMessage, failure.Message);
    }

    [Fact]
    public async Task No_more_files_run_at_once_than_the_chosen_tool_allows()
    {
        var tool = new RecordingTool { MaxParallel = 1, Delay = TimeSpan.FromMilliseconds(50) };
        using var op = new PdfToolsOperation(_ => tool).WithPrompt(new FakePrompt(new PdfToolChoice(Numbers, new Dictionary<string, string>())));

        await Task.WhenAll(Enumerable.Range(1, 4).Select(i => op.RunAsync($@"C:\{i}.pdf", CancellationToken.None)));

        Assert.Equal(1, tool.MostAtOnce);
    }

    [Fact]
    public async Task A_cancelled_batch_stops_files_waiting_for_the_answer()
    {
        var prompt = new FakePrompt(null) { Wait = true };
        using var op = new PdfToolsOperation(_ => new RecordingTool()).WithPrompt(prompt);
        using var cancel = new CancellationTokenSource();

        var waiting = op.RunAsync(@"C:\a.pdf", cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class FakePrompt(PdfToolChoice? answer) : IPdfToolPrompt
    {
        public int Asked { get; private set; }

        public bool Wait { get; init; }

        public async Task<PdfToolChoice?> AskAsync(CancellationToken ct)
        {
            Asked++;
            if (Wait)
                await Task.Delay(Timeout.Infinite, ct);
            await Task.Yield();
            return answer;
        }
    }

    private sealed class RecordingTool : IOperation
    {
        private int _running;

        public List<string> Ran { get; } = [];

        public int MostAtOnce { get; private set; }

        public TimeSpan Delay { get; init; }

        public string Id => "pdf.page-numbers";

        public string MenuPath => "";

        public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

        public int MaxParallel { get; init; } = 4;

        public async Task<string> RunAsync(string inputPath, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _running);
            lock (Ran)
            {
                MostAtOnce = Math.Max(MostAtOnce, now);
                Ran.Add(inputPath);
            }

            await Task.Delay(Delay, ct);
            Interlocked.Decrement(ref _running);
            return Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + " (numbered).pdf");
        }
    }
}
