using System.Diagnostics;
using System.Runtime.CompilerServices;
using Flevert.Core.Operations;

namespace Flevert.Core.Tests;

/// <summary>Operations that join every file of a batch into one output (IBatchOperation, ADR-0007 part 2).</summary>
public sealed class RunnerBatchOperationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    private Runner NewRunner() => new(new RunLog(_logs.Path));

    [Fact]
    public async Task Runs_the_operation_once_after_the_batch_is_complete_with_every_file_in_natural_order()
    {
        var ten = _dir.File("ảnh 10.txt");
        var two = _dir.File("ảnh 2.txt");
        var one = _dir.File("ảnh 1.txt");
        var source = new SlowSource([ten, two, one]);
        var op = new RecordingJoin(onRun: _ => Assert.True(source.Finished, "the operation ran before the batch was complete"));

        var outcome = await NewRunner().RunAsync(op, source.ReadAllAsync(), progress: null, CancellationToken.None);

        Assert.True(outcome.NoFailures);
        Assert.Equal([one, two, ten], Assert.Single(op.Calls));
    }

    [Fact]
    public async Task Dev_join_writes_one_file_named_after_the_first_with_every_file_in_natural_order()
    {
        var ten = _dir.File("ảnh 10.txt", "mười\r\n");
        var two = _dir.File("ảnh 2.txt", "hai\r\n");
        var one = _dir.File("ảnh 1.txt", "một\r\n");

        var outcome = await NewRunner().RunAsync(new DevJoin(), [ten, two, one], progress: null, CancellationToken.None);

        Assert.True(outcome.OneOutput);
        Assert.Equal(1, outcome.Total);
        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded);
        Assert.Equal(_dir.Combine("ảnh 1 (+2).txt"), result.Output);
        Assert.Equal("một\r\nhai\r\nmười\r\n", await File.ReadAllTextAsync(result.Output!));
        Assert.Equal("Done: ảnh 1 (+2).txt", BatchSummary.Describe(outcome, _logs.Path).Text);
        Assert.Equal("một\r\n", await File.ReadAllTextAsync(one)); // inputs unchanged
    }

    [Fact]
    public async Task Dev_join_of_one_file_names_its_output_joined()
    {
        var only = _dir.File("Báo cáo.txt", "nội dung");

        var outcome = await NewRunner().RunAsync(new DevJoin(), [only], progress: null, CancellationToken.None);

        Assert.Equal(_dir.Combine("Báo cáo (joined).txt"), Assert.Single(outcome.Results).Output);
        Assert.Equal("nội dung", await File.ReadAllTextAsync(_dir.Combine("Báo cáo (joined).txt")));
    }

    [Fact]
    public async Task Output_goes_in_the_folder_of_the_first_file()
    {
        Directory.CreateDirectory(_dir.Combine("a"));
        Directory.CreateDirectory(_dir.Combine("b"));
        var later = _dir.File(@"a\x 10.txt");
        var first = _dir.File(@"b\x 2.txt");

        var outcome = await NewRunner().RunAsync(new DevJoin(), [later, first], progress: null, CancellationToken.None);

        Assert.Equal(_dir.Combine(@"b\x 2 (+1).txt"), Assert.Single(outcome.Results).Output);
    }

    [Fact]
    public async Task A_file_of_another_type_fails_the_whole_output_and_the_notification_names_it()
    {
        var files = new[] { _dir.File("1.txt"), _dir.File("2.md"), _dir.File("3.txt") };
        var op = new RecordingJoin();

        var outcome = await NewRunner().RunAsync(op, files, progress: null, CancellationToken.None);

        Assert.Empty(op.Calls);
        Assert.False(outcome.NoFailures);
        var result = Assert.Single(outcome.Results);
        Assert.True(result.Failed);
        Assert.Equal(files[1], result.Input);
        Assert.Equal(["1.txt", "2.md", "3.txt"], _dir.FileNames()); // no partial output, no .partial file
        var summary = BatchSummary.Describe(outcome, _logs.Path);
        Assert.True(summary.HasFailures);
        Assert.True(summary.ShowsLog);
        Assert.StartsWith("Failed: 2.md: File type .md is not supported by test.join.", summary.Text, StringComparison.Ordinal);
        Assert.EndsWith(_logs.Path, summary.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_whose_process_is_still_starting_did_not_reach_flevert_in_time_and_nothing_is_joined()
    {
        var one = _dir.File("1.txt");
        var two = _dir.Combine("2.txt");
        var op = new RecordingJoin();

        var outcome = await NewRunner().RunAsync(op, new[] { one }.ToAsyncEnumerable(), progress: null, CancellationToken.None,
            () => [new MissingFile(two, StillRunning: true)]);

        Assert.Empty(op.Calls);
        var result = Assert.Single(outcome.Results);
        Assert.Equal("2.txt did not reach Flevert in time, so nothing was joined. Select the files again.", result.Error);
        Assert.Contains(File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path))), line =>
            line.Contains("\t" + two + "\tfailed\t", StringComparison.Ordinal)
            && line.Contains("\tdid not reach Flevert in time: it arrived too late to be included", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_whose_process_ended_never_reached_flevert()
    {
        var one = _dir.File("1.txt");
        var op = new RecordingJoin();

        var outcome = await NewRunner().RunAsync(op, new[] { one }.ToAsyncEnumerable(), progress: null, CancellationToken.None,
            () => [new MissingFile(_dir.Combine("2.txt"), StillRunning: true), new MissingFile(_dir.Combine("3.txt"), StillRunning: false)]);

        Assert.Empty(op.Calls);
        Assert.Equal("2.txt and 1 more file never reached Flevert, so nothing was joined. Select the files again.", Assert.Single(outcome.Results).Error);
    }

    [Fact]
    public void Files_a_batch_accounted_for_are_logged_as_skipped_without_anything_else()
    {
        var file = _dir.Combine("late.txt");

        NewRunner().LogAccountedFor(new DevJoin(), [file]);

        var line = Assert.Single(File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path))));
        Assert.Contains("\t" + file + "\tskipped\t", line, StringComparison.Ordinal);
        Assert.Contains("its batch had been cancelled", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_fails_the_whole_output()
    {
        var present = _dir.File("1.txt");
        var missing = _dir.Combine("2.txt");
        var op = new RecordingJoin();

        var outcome = await NewRunner().RunAsync(op, [present, missing], progress: null, CancellationToken.None);

        Assert.Empty(op.Calls);
        Assert.Equal(missing, Assert.Single(outcome.Results).Input);
        Assert.Contains("2.txt", BatchSummary.Describe(outcome, _logs.Path).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_that_is_not_a_plain_file_fails_the_whole_output()
    {
        var op = new RecordingJoin();

        var outcome = await NewRunner().RunAsync(op, [_dir.File("1.txt"), @"\\.\pipe\flevert-test.txt"], progress: null, CancellationToken.None);

        Assert.Empty(op.Calls);
        Assert.Equal(@"\\.\pipe\flevert-test.txt", Assert.Single(outcome.Results).Input);
    }

    [Fact]
    public async Task Every_rejected_file_is_logged_and_nothing_else()
    {
        var files = new[] { _dir.File("1.txt"), _dir.File("2.md"), _dir.Combine("3.txt") };

        await NewRunner().RunAsync(new RecordingJoin(), files, progress: null, CancellationToken.None);

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path, "flevert-*.log")));
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.Contains("\tfailed\t", line, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("2.md", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("3.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_input_stays_pinned_while_the_operation_runs()
    {
        var files = new[] { _dir.File("1.txt"), _dir.File("2.txt"), _dir.File("3.txt") };
        var stillThere = new List<bool>();
        var op = new RecordingJoin(onRun: inputs =>
        {
            // Another process tries to delete every input in the middle of the run.
            foreach (var input in inputs)
            {
                using var del = Process.Start(new ProcessStartInfo("cmd.exe")
                {
                    ArgumentList = { "/c", "del", "/f", "/q", input },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                })!;
                del.WaitForExit();
                stillThere.Add(File.Exists(input));
            }
        });

        var outcome = await NewRunner().RunAsync(op, files, progress: null, CancellationToken.None);

        Assert.True(outcome.NoFailures);
        Assert.Equal([true, true, true], stillThere);
        foreach (var file in files)
            File.Delete(file); // released once the run is over
    }

    [Fact]
    public async Task The_same_file_given_twice_is_joined_once()
    {
        var file = _dir.File("ảnh 1.txt");
        var op = new RecordingJoin();

        await NewRunner().RunAsync(op, [file, _dir.Combine("ẢNH 1.TXT")], progress: null, CancellationToken.None);

        Assert.Equal([file], Assert.Single(op.Calls));
    }

    [Fact]
    public async Task Progress_says_the_join_is_waiting_for_its_files_before_any_arrives()
    {
        // Followers can take seconds to hand over their files; a blank window looks like a hang.
        var file = _dir.File("1.txt");
        var seen = new List<BatchProgress>();
        var arrive = new TaskCompletionSource();

        async IAsyncEnumerable<string> Source()
        {
            await arrive.Task;
            yield return file;
        }

        var run = NewRunner().RunAsync(new DevJoin(), Source(), new SyncProgress<BatchProgress>(seen.Add), CancellationToken.None);
        var beforeAnyFile = seen.ToList();
        arrive.SetResult();
        await run;

        Assert.Equal([new BatchProgress(0, 1, null, Status: "Waiting for the selected files…")], beforeAnyFile);
        Assert.Equal(new BatchProgress(0, 1, file), seen[1]);
    }

    [Fact]
    public async Task Progress_names_the_first_file_and_carries_the_operation_steps_in_order()
    {
        var files = new[] { _dir.File("c 3.txt"), _dir.File("a 1.txt"), _dir.File("b 2.txt") };
        var seen = new List<BatchProgress>();

        await NewRunner().RunAsync(new DevJoin(), files, new SyncProgress<BatchProgress>(seen.Add), CancellationToken.None);

        Assert.Equal(new BatchProgress(0, 1, null, Status: "Waiting for the selected files…"), seen[0]);
        Assert.Equal(new BatchProgress(0, 1, files[1]), seen[1]);
        Assert.Equal([1, 2, 3], seen.Skip(2).SkipLast(1).Select(p => p.Within!.Position));
        Assert.All(seen.Skip(2).SkipLast(1), p =>
        {
            Assert.Equal(new FileProgress("file", p.Within!.Position, 3), p.Within);
            Assert.Equal(files[1], p.Current);
            Assert.Equal(0, p.Completed);
            Assert.Equal(1, p.Total);
        });
        Assert.Equal(new BatchProgress(1, 1, null), seen[^1]);
    }

    [Fact]
    public async Task Cancelling_while_the_operation_runs_leaves_no_output_and_no_partial_file()
    {
        var files = new[] { _dir.File("1.txt"), _dir.File("2.txt"), _dir.File("3.txt") };
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<BatchProgress>(p =>
        {
            if (p.Within?.Position == 2)
                cts.Cancel();
        });

        var outcome = await NewRunner().RunAsync(new DevJoin(), files, progress, cts.Token);

        Assert.Empty(outcome.Results);
        Assert.Equal(1, outcome.Total);
        Assert.False(outcome.NoFailures);
        Assert.Equal("Cancelled.", BatchSummary.Describe(outcome, _logs.Path).Text);
        Assert.Equal(["1.txt", "2.txt", "3.txt"], _dir.FileNames());
        Assert.Empty(Directory.GetFileSystemEntries(_dir.Path, ".flevert-*"));
        Assert.Contains("\tcancelled", File.ReadAllText(Assert.Single(Directory.GetFiles(_logs.Path, "flevert-*.log"))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_join_that_finishes_while_it_is_given_time_to_stop_is_reported_and_logged()
    {
        // The output exists: saying "Cancelled." and logging nothing would hide a file the user now has.
        var files = new[] { _dir.File("1.txt", "một"), _dir.File("2.txt", "hai") };
        using var cts = new CancellationTokenSource();
        var op = new StubbornJoin(onStart: cts.Cancel, finishAfter: TimeSpan.FromMilliseconds(300));

        var outcome = await NewRunner().RunAsync(op, files, progress: null, cts.Token);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Succeeded);
        Assert.Equal(_dir.Combine("1 (stubborn).txt"), result.Output);
        Assert.True(File.Exists(result.Output));
        Assert.Equal("Done: 1 (stubborn).txt", BatchSummary.Describe(outcome, _logs.Path).Text);
        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path, "flevert-*.log")));
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Equal("ok", line.Split('\t')[4]);
            Assert.EndsWith("\t" + result.Output, line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_join_that_ignores_cancellation_is_abandoned_after_its_grace_period_and_its_inputs_are_released()
    {
        var files = new[] { _dir.File("1.txt"), _dir.File("2.txt") };
        using var cts = new CancellationTokenSource();
        var never = new TaskCompletionSource();
        var op = new StubbornJoin(onStart: cts.Cancel, finishAfter: TimeSpan.Zero, hold: never.Task);
        var timer = Stopwatch.StartNew();
        try
        {
            var outcome = await NewRunner().RunAsync(op, files, progress: null, cts.Token).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(15));
            Assert.Empty(outcome.Results);
            Assert.Equal("Cancelled.", BatchSummary.Describe(outcome, _logs.Path).Text);
            Assert.Contains("\tcancelled", File.ReadAllText(Assert.Single(Directory.GetFiles(_logs.Path, "flevert-*.log"))), StringComparison.Ordinal);
            foreach (var file in files)
                File.Delete(file); // unpinned once the operation is abandoned
            Assert.Empty(_dir.FileNames());
        }
        finally
        {
            never.SetResult();
        }
    }

    [Fact]
    public async Task Cancelling_while_files_are_still_arriving_runs_nothing()
    {
        using var cts = new CancellationTokenSource();
        var op = new RecordingJoin();

        var run = NewRunner().RunAsync(op, Endless(_dir.File("1.txt")), progress: null, cts.Token);
        await cts.CancelAsync();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(op.Calls);
        Assert.Empty(outcome.Results);
        Assert.False(outcome.NoFailures);
    }

    [Fact]
    public async Task An_input_the_operation_cannot_use_fails_the_output_and_is_named()
    {
        var files = new[] { _dir.File("1.txt"), _dir.File("2.txt") };
        var op = new RecordingJoin(onRun: inputs => throw BatchInputException.ForInput(inputs[1], "This file is damaged."));

        var outcome = await NewRunner().RunAsync(op, files, progress: null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Failed);
        Assert.Equal(files[1], result.Input);
        Assert.StartsWith("Failed: 2.txt: This file is damaged.", BatchSummary.Describe(outcome, _logs.Path).Text, StringComparison.Ordinal);
        Assert.Contains(nameof(BatchInputException), result.Error, StringComparison.Ordinal); // the log keeps the stack
    }

    [Fact]
    public async Task A_batch_input_exception_that_names_no_input_does_not_blame_the_first_file()
    {
        // The standard constructors carry no path: the notification must not pin the fault on whichever file sorts first.
        var files = new[] { _dir.File("1.txt"), _dir.File("2.txt") };
        var op = new RecordingJoin(onRun: _ => throw new BatchInputException("One of the photos is damaged."));

        var outcome = await NewRunner().RunAsync(op, files, progress: null, CancellationToken.None);

        Assert.True(Assert.Single(outcome.Results).Failed);
        Assert.StartsWith("Failed: One of the photos is damaged.", BatchSummary.Describe(outcome, _logs.Path).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_input_exception_for_an_input_keeps_the_path_apart_from_the_message()
    {
        var inner = new InvalidDataException("bad marker");

        var ex = BatchInputException.ForInput(@"C:\ảnh 2.jpg", "This photo is damaged.", inner);

        Assert.Equal(@"C:\ảnh 2.jpg", ex.InputPath);
        Assert.Equal("This photo is damaged.", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Null(new BatchInputException("This photo is damaged.").InputPath);
    }

    [Fact]
    public async Task A_failure_not_tied_to_one_input_fails_the_output_with_its_message()
    {
        var op = new RecordingJoin(onRun: _ => throw new IOException("There is not enough space on the disk."));

        var outcome = await NewRunner().RunAsync(op, [_dir.File("1.txt"), _dir.File("2.txt")], progress: null, CancellationToken.None);

        Assert.True(Assert.Single(outcome.Results).Failed);
        Assert.False(outcome.NoFailures);
        Assert.StartsWith("Failed: There is not enough space on the disk.", BatchSummary.Describe(outcome, _logs.Path).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_operation_that_skips_reports_skipped_and_no_failure()
    {
        var op = new RecordingJoin(onRun: _ => throw new OperationSkippedException("Nothing to join."));

        var outcome = await NewRunner().RunAsync(op, [_dir.File("1.txt")], progress: null, CancellationToken.None);

        Assert.True(Assert.Single(outcome.Results).Skipped);
        Assert.True(outcome.NoFailures);
        Assert.Equal("Skipped: Nothing to join.", BatchSummary.Describe(outcome, _logs.Path).Text);
    }

    [Fact]
    public async Task A_batch_that_received_no_file_fails_instead_of_reporting_done()
    {
        // "Done: 0 files" would claim success for a join that wrote nothing.
        var op = new RecordingJoin();

        var outcome = await NewRunner().RunAsync(op, [], progress: null, CancellationToken.None);

        Assert.Empty(op.Calls);
        Assert.False(outcome.NoFailures);
        var summary = BatchSummary.Describe(outcome, _logs.Path);
        Assert.True(summary.HasFailures);
        Assert.StartsWith("Failed: No files were received.", summary.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_join_logs_every_input_with_the_output()
    {
        var files = new[] { _dir.File("1.txt"), _dir.File("2.txt") };

        await NewRunner().RunAsync(new DevJoin(), files, progress: null, CancellationToken.None);

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path, "flevert-*.log")));
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.EndsWith("\t" + _dir.Combine("1 (+1).txt"), line, StringComparison.Ordinal));
        Assert.Contains("\t" + files[0] + "\tok\t", lines[0], StringComparison.Ordinal);
        Assert.Contains("\t" + files[1] + "\tok\t", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_catalog_holds_both_kinds_of_operation()
    {
        var catalog = new OperationCatalog([new DevCopy(), new DevJoin()]);

        Assert.IsType<DevCopy>(catalog.Find("dev.copy"));
        Assert.IsType<DevJoin>(catalog.Find("dev.join"));
    }

    [Fact]
    public void A_catalog_refuses_an_operation_of_both_kinds()
    {
        // The runner would not know whether to convert each file or join them.
        Assert.Throws<ArgumentException>(() => new OperationCatalog([new BothKinds()]));
    }

    private static async IAsyncEnumerable<string> Endless(string first, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return first;
        await Task.Delay(Timeout.Infinite, ct);
    }

    /// <summary>Yields files with pauses, the way a batch leader does while followers start.</summary>
    private sealed class SlowSource(IReadOnlyList<string> files)
    {
        public bool Finished { get; private set; }

        public async IAsyncEnumerable<string> ReadAllAsync()
        {
            foreach (var file in files)
            {
                await Task.Delay(30);
                yield return file;
            }

            await Task.Delay(30);
            Finished = true;
        }
    }

    private sealed class RecordingJoin(Action<IReadOnlyList<string>>? onRun = null) : IBatchOperation
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public string Id => "test.join";

        public string MenuPath => "Test/Join";

        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct)
        {
            Calls.Add([.. inputPaths]);
            onRun?.Invoke(inputPaths);
            return Task.FromResult(inputPaths[0]);
        }
    }

    /// <summary>Ignores the token: the cancelled batch must wait for it a while, then let go.</summary>
    private sealed class StubbornJoin(Action onStart, TimeSpan finishAfter, Task? hold = null) : IBatchOperation
    {
        public string Id => "test.stubborn-join";

        public string MenuPath => "Test/Stubborn join";

        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct)
        {
            onStart();
            if (hold is not null)
            {
                await hold.ConfigureAwait(false);
                throw new OperationCanceledException(ct); // released by the test once it is done; writes nothing
            }

            await Task.Delay(finishAfter, CancellationToken.None).ConfigureAwait(false);
            var desired = Path.Combine(Path.GetDirectoryName(inputPaths[0])!, Path.GetFileNameWithoutExtension(inputPaths[0]) + " (stubborn).txt");
            return await OutputPath.WriteAtomicallyAsync(desired, (temp, _) => File.WriteAllTextAsync(temp, "joined", CancellationToken.None), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private sealed class BothKinds : IOperation, IBatchOperation
    {
        public string Id => "test.both";

        public string MenuPath => "Test/Both";

        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public Task<string> RunAsync(string inputPath, CancellationToken ct) => Task.FromResult(inputPath);

        public Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct) =>
            Task.FromResult(inputPaths[0]);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
