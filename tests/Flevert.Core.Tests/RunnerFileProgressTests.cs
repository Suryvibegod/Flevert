using Flevert.Core.Operations;

namespace Flevert.Core.Tests;

/// <summary>Progress inside one file ("page 37 of 200") and the folder output of a one-to-many operation.</summary>
public sealed class RunnerFileProgressTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Progress_inside_a_file_reaches_the_batch_progress_in_order()
    {
        var file = _dir.File("report.txt");
        var seen = new List<BatchProgress>();

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new PagesOp(5), [file], new SyncProgress<BatchProgress>(seen.Add), CancellationToken.None);

        Assert.True(Assert.Single(outcome.Results).Succeeded);
        var within = seen.Where(p => p.Within is not null).ToList();
        Assert.Equal([1, 2, 3, 4, 5], within.Select(p => p.Within!.Position));
        Assert.All(within, p =>
        {
            Assert.Equal(file, p.Current);
            Assert.Equal("page", p.Within!.Unit);
            Assert.Equal(5, p.Within.Total);
            Assert.Equal(seen[0].Completed, p.Completed); // the batch counts as at the file's start
            Assert.Equal(seen[0].Total, p.Total);
        });
        // Started, five pages, finished: the page reports come between the file's start and its end.
        Assert.Equal(7, seen.Count);
        Assert.Null(seen[0].Within);
        Assert.Equal(1, seen[^1].Completed);
        Assert.Null(seen[^1].Within);
    }

    [Fact]
    public async Task Progress_reported_after_the_file_finished_is_dropped()
    {
        // Otherwise a late report would bring back "page 5 of 5" for a file the window already counts as done.
        var file = _dir.File("report.txt");
        var seen = new List<BatchProgress>();
        var op = new PagesOp(1);

        await new Runner(new RunLog(_logs.Path)).RunAsync(op, [file], new SyncProgress<BatchProgress>(seen.Add), CancellationToken.None);
        var count = seen.Count;
        op.Kept!.Report(new FileProgress("page", 1, 1));

        Assert.Equal(count, seen.Count);
    }

    [Fact]
    public async Task An_operation_without_progress_runs_through_the_overload_that_takes_it()
    {
        IOperation copy = new DevCopy();

        var output = await copy.RunAsync(_dir.File("a.txt", "hi"), new SyncProgress<FileProgress>(_ => { }), CancellationToken.None);

        Assert.Equal("hi", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Dev_split_writes_a_folder_of_numbered_pages_and_reports_each_page()
    {
        var file = _dir.File("ảnh 1.txt");
        var pages = new List<FileProgress>();

        var output = await new DevSplit(pages: 12, pageDelay: TimeSpan.Zero)
            .RunAsync(file, new SyncProgress<FileProgress>(pages.Add), CancellationToken.None);

        Assert.Equal(_dir.Combine("ảnh 1 (pages)"), output);
        Assert.Equal([.. Enumerable.Range(1, 12).Select(n => $"ảnh 1 {n:D2}.txt")], Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(Enumerable.Range(1, 12), pages.Select(p => p.Position));
        Assert.All(pages, p => Assert.Equal(12, p.Total));
    }

    [Fact]
    public async Task Dev_split_takes_the_next_folder_name_when_the_first_is_taken()
    {
        var file = _dir.File("a.txt");
        var split = new DevSplit(pages: 2, pageDelay: TimeSpan.Zero);

        await split.RunAsync(file, CancellationToken.None);
        var second = await split.RunAsync(file, CancellationToken.None);

        Assert.Equal(_dir.Combine("a (pages) (1)"), second);
    }

    [Fact]
    public async Task Dev_split_keeps_page_names_within_255_characters_for_a_long_input_name()
    {
        var file = _dir.File(new string('n', 251) + ".txt");

        var output = await new DevSplit(pages: 2, pageDelay: TimeSpan.Zero).RunAsync(file, CancellationToken.None);

        Assert.All(Directory.GetFiles(output), page => Assert.True(Path.GetFileName(page).Length <= 255));
        Assert.Equal(2, Directory.GetFiles(output).Length);
    }

    // Stems of 248 to 251 characters make "<stem> (pages)" longer than 255: the stem is shortened, not the suffix.
    [Fact]
    public async Task Dev_split_keeps_the_pages_suffix_whole_for_a_long_input_name()
    {
        var file = _dir.File(new string('n', 250) + ".txt");
        var split = new DevSplit(pages: 1, pageDelay: TimeSpan.Zero);

        var first = Path.GetFileName(await split.RunAsync(file, CancellationToken.None));
        var second = Path.GetFileName(await split.RunAsync(file, CancellationToken.None));

        Assert.EndsWith("n (pages)", first, StringComparison.Ordinal);
        Assert.Equal(255, first.Length);
        Assert.EndsWith("n (pages) (1)", second, StringComparison.Ordinal);
        Assert.Equal(255, second.Length);
    }

    // A page must never overwrite or follow something already at its name: another account with write access to the output
    // folder could plant a link or a file at the next page's name. The writer opens each page CreateNew, FileShare.None.
    [Fact]
    public async Task Dev_split_fails_rather_than_overwrite_an_entry_planted_at_a_page_name()
    {
        var file = _dir.File("report.txt");
        var split = new DevSplit(pages: 5, pageDelay: TimeSpan.FromMilliseconds(150));

        // While the pages are written one by one, find the temporary folder and plant the next page's name. CreateNew, and
        // the first name not written yet: a planter starved past one page's delay plants a later page instead of
        // overwriting one the writer already made (which would let the run succeed).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var plant = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var partial = Directory.EnumerateDirectories(_dir.Path, ".flevert-*").FirstOrDefault();
                if (partial is not null && File.Exists(Path.Combine(partial, "report 1.txt")))
                {
                    for (var page = 2; page <= 5; page++)
                    {
                        try
                        {
                            using var planted = new FileStream(Path.Combine(partial, $"report {page}.txt"), FileMode.CreateNew);
                            return page;
                        }
                        catch (IOException) { }
                    }

                    return 0;
                }

                await Task.Delay(10, CancellationToken.None);
            }

            return 0;
        }, CancellationToken.None);

        var run = Assert.ThrowsAnyAsync<IOException>(() => split.RunAsync(file, CancellationToken.None));
        Assert.NotEqual(0, await plant);
        await run;

        // The failed run leaves nothing beside the input.
        Assert.Equal(["report.txt"], _dir.FileNames());
        Assert.Empty(Directory.GetDirectories(_dir.Path));
    }

    [Fact]
    public async Task Cancelling_midway_through_a_folder_output_leaves_no_folder_behind()
    {
        var file = _dir.File("report.txt");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<BatchProgress>(p => { if (p.Within?.Position == 10) cts.Cancel(); });

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevSplit(pages: 50, pageDelay: TimeSpan.FromMilliseconds(5)), [file], progress, cts.Token);

        Assert.Empty(outcome.Results);
        // The runner lets go of a cancelled file at once rather than awaiting it (RunAsync's WaitAsync); the operation
        // stops at its next token check and only then deletes its folder. The progress window stays up longer than that.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string[] entries;
        while ((entries = [.. Directory.GetFileSystemEntries(_dir.Path).Select(e => Path.GetFileName(e))]).Length > 1
            && timer.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        Assert.Equal(["report.txt"], entries);
    }

    /// <summary>Reports each page synchronously and keeps the progress it was given.</summary>
    private sealed class PagesOp(int pages) : IOperation
    {
        public IProgress<FileProgress>? Kept { get; private set; }
        public string Id => "test.pages";
        public string MenuPath => "Test/Pages";
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

        public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
        {
            Kept = progress;
            for (var page = 1; page <= pages; page++)
                progress?.Report(new FileProgress("page", page, pages));
            return Task.FromResult(inputPath);
        }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
