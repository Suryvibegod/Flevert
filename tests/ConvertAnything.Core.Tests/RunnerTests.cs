using ConvertAnything.Core.Operations;

namespace ConvertAnything.Core.Tests;

public sealed class RunnerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Dev_copy_creates_copy_next_to_original_and_keeps_original_intact()
    {
        var source = _dir.File("ảnh 1.txt", "nội dung");

        var output = await new DevCopy().RunAsync(source, CancellationToken.None);

        Assert.Equal(_dir.Combine("ảnh 1 (copy).txt"), output);
        Assert.Equal("nội dung", await File.ReadAllTextAsync(output));
        Assert.Equal("nội dung", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task Runner_continues_after_a_failing_file_and_reports_each_result()
    {
        var good = _dir.File("good.txt");
        var missing = _dir.Combine("missing.txt");

        var results = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [missing, good], progress: null, CancellationToken.None);

        Assert.Collection(results,
            r => { Assert.Equal(missing, r.Input); Assert.False(r.Succeeded); Assert.NotNull(r.Error); },
            r => { Assert.Equal(good, r.Input); Assert.True(r.Succeeded); Assert.True(File.Exists(r.Output)); });
    }

    [Fact]
    public async Task Runner_writes_one_log_line_per_file()
    {
        var good = _dir.File("good.txt");
        var missing = _dir.Combine("missing.txt");

        await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [good, missing], progress: null, CancellationToken.None);

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path, "ca-*.log")));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\tdev.copy\t", lines[0], StringComparison.Ordinal);
        Assert.Contains("\tok\t", lines[0], StringComparison.Ordinal);
        Assert.Contains("\tfailed\t", lines[1], StringComparison.Ordinal);
        Assert.Contains("missing.txt", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_stops_before_next_file_when_cancelled()
    {
        var first = _dir.File("1.txt");
        var second = _dir.File("2.txt");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<BatchProgress>(p => { if (p.Completed == 1) cts.Cancel(); });

        var results = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [first, second], progress, cts.Token);

        Assert.Single(results);
        Assert.False(File.Exists(_dir.Combine("2 (copy).txt")));
    }

    [Fact]
    public async Task File_interrupted_by_cancel_is_not_reported_as_a_result()
    {
        var first = _dir.File("1.txt");
        var second = _dir.File("2.txt");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<BatchProgress>(p => { if (p.Current == second) cts.Cancel(); });

        var results = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevSlowCopy(), [first, second], progress, cts.Token);

        Assert.Equal([first], results.Select(r => r.Input));
        Assert.False(File.Exists(_dir.Combine("2 (copy).txt")));
    }

    [Fact]
    public void Catalog_finds_operations_by_id()
    {
        var catalog = new OperationCatalog([new DevCopy()]);

        Assert.IsType<DevCopy>(catalog.Find("dev.copy"));
        Assert.Null(catalog.Find("nope"));
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
