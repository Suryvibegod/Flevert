namespace ConvertAnything.Core.Tests;

/// <summary>A batch runs several files at once (docs/specs/core.md, "Xử lý song song trong một lô") with the same guarantees.</summary>
public sealed class RunnerParallelTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Runs_up_to_max_parallel_files_at_once_and_never_more()
    {
        var files = Enumerable.Range(1, 7).Select(i => _dir.File($"{i}.txt")).ToList();
        var op = new GatedOp(openWhenRunning: 3);

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(op, files, progress: null, CancellationToken.None, maxParallel: 3)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(3, op.MaxRunning);
        Assert.Equal(7, outcome.Results.Count(r => r.Succeeded));
        Assert.True(outcome.NoFailures);
    }

    [Fact]
    public async Task One_at_a_time_by_default()
    {
        // Each file takes a while, so files run in parallel would be seen overlapping.
        var files = Enumerable.Range(1, 4).Select(i => _dir.File($"{i}.txt")).ToList();
        var op = new GatedOp(openWhenRunning: int.MaxValue, hold: TimeSpan.FromMilliseconds(150));

        await new Runner(new RunLog(_logs.Path)).RunAsync(op, files, progress: null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, op.MaxRunning);
    }

    [Fact]
    public void An_operation_runs_one_file_at_a_time_unless_it_says_otherwise()
    {
        // Future media operations call ffmpeg, which already uses every core; images use one core each.
        Assert.Equal(1, ((IOperation)new GatedOp(1)).MaxParallel);
        Assert.True(new Operations.DevSlowCopy().MaxParallel > 1);
        Assert.True(new Operations.DevCopy().MaxParallel > 1);
    }

    [Fact]
    public async Task A_failing_file_does_not_stop_the_others_running_beside_it()
    {
        var files = Enumerable.Range(1, 6).Select(i => _dir.File($"{i}.txt")).ToList();
        var op = new GatedOp(openWhenRunning: 2, failOn: "3.txt");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(op, files, progress: null, CancellationToken.None, maxParallel: 2)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(6, outcome.Results.Count);
        Assert.Equal(["3.txt"], outcome.Results.Where(r => r.Failed).Select(r => Path.GetFileName(r.Input)));
        Assert.Equal(6, File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path, "ca-*.log"))).Length);
    }

    [Fact]
    public async Task A_path_received_twice_still_runs_once()
    {
        var file = _dir.File("a.txt");
        var other = _dir.File("b.txt");
        var op = new GatedOp(openWhenRunning: 2);

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(op, [file, other, file], progress: null, CancellationToken.None, maxParallel: 4)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, outcome.Results.Count);
        Assert.Equal(2, op.Started);
    }

    [Fact]
    public async Task Cancel_starts_no_new_file_and_counts_the_interrupted_ones_as_unprocessed()
    {
        var files = Enumerable.Range(1, 6).Select(i => _dir.File($"{i}.txt")).ToList();
        using var cts = new CancellationTokenSource();
        // Never opens by itself: both running files are waiting when the batch is cancelled.
        var op = new GatedOp(openWhenRunning: int.MaxValue, onRunning: running => { if (running == 2) cts.Cancel(); });

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(op, files, progress: null, cts.Token, maxParallel: 2)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, op.Started);
        Assert.Empty(outcome.Results);
        Assert.Equal(6, outcome.Total);
        Assert.False(outcome.NoFailures);
    }

    [Fact]
    public async Task Reports_how_many_files_are_running_so_the_leader_stays_open()
    {
        var files = Enumerable.Range(1, 5).Select(i => _dir.File($"{i}.txt")).ToList();
        var seen = new List<int>();
        var op = new GatedOp(openWhenRunning: 3);

        await new Runner(new RunLog(_logs.Path)).RunAsync(
            op, files.ToAsyncEnumerable(), pending: () => 0, progress: null, CancellationToken.None,
            maxParallel: 3, running: n => { lock (seen) seen.Add(n); }).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(3, seen.Max());
        Assert.Equal(0, seen[^1]);
        // Raised before the next file is requested, lowered only after a result is recorded.
        Assert.Equal(1, seen[0]);
    }

    /// <summary>
    /// Holds every file until <paramref name="openWhenRunning"/> run at the same time (or 5 s pass), so concurrency is
    /// observed directly rather than inferred from timing.
    /// </summary>
    private sealed class GatedOp(int openWhenRunning, string? failOn = null, Action<int>? onRunning = null, TimeSpan? hold = null) : IOperation
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private int _maxRunning;
        private int _started;

        public int MaxRunning => Volatile.Read(ref _maxRunning);

        public int Started => Volatile.Read(ref _started);

        public string Id => "dev.gated";

        public string MenuPath => "Dev/Gated";

        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<string> RunAsync(string inputPath, CancellationToken ct)
        {
            Interlocked.Increment(ref _started);
            var running = Interlocked.Increment(ref _running);
            int max;
            while ((max = Volatile.Read(ref _maxRunning)) < running && Interlocked.CompareExchange(ref _maxRunning, running, max) != max)
            {
            }

            try
            {
                onRunning?.Invoke(running);
                if (running >= openWhenRunning)
                    _open.TrySetResult();
                await Task.WhenAny(_open.Task, Task.Delay(hold ?? TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (Path.GetFileName(inputPath) == failOn)
                    throw new IOException("bad file");
                return inputPath;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }
}
