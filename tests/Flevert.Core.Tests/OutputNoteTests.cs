namespace Flevert.Core.Tests;

/// <summary>
/// A remark an operation makes about an output it wrote (T18: "the digital signature is not valid in the output"), which
/// reaches the notification and the log line of that file only.
/// </summary>
public sealed class OutputNoteTests : IDisposable
{
    private const string LogDir = @"D:\logs";
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task A_note_made_while_converting_a_file_belongs_to_that_file_only_even_in_parallel()
    {
        var files = Enumerable.Range(1, 8).Select(i => _dir.File($"ảnh {i}.txt")).ToList();

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new NotingCopy(), files, progress: null, CancellationToken.None, maxParallel: 4);

        Assert.True(outcome.NoFailures);
        foreach (var result in outcome.Results)
        {
            var number = int.Parse(Path.GetFileNameWithoutExtension(result.Input)[4..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(number % 2 == 0 ? $"Even file {number}." : null, result.Note);
        }
    }

    [Fact]
    public async Task A_note_is_written_at_the_end_of_its_log_line()
    {
        var even = _dir.File("ảnh 2.txt");
        var odd = _dir.File("ảnh 3.txt");

        await new Runner(new RunLog(_logs.Path)).RunAsync(new NotingCopy(), [even, odd], progress: null, CancellationToken.None);

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path, "flevert-*.log")));
        Assert.Single(lines, line => line.EndsWith("\tEven file 2.", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains("ảnh 3.txt", StringComparison.Ordinal) && line.Split('\t').Length == 7);
    }

    [Fact]
    public async Task A_joined_output_carries_the_note_its_operation_made()
    {
        var one = _dir.File("a 1.txt");
        var two = _dir.File("a 2.txt");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(new NotingJoin(), [one, two], progress: null, CancellationToken.None);

        Assert.Equal("Joined 2 files.", Assert.Single(outcome.Results).Note);
    }

    [Fact]
    public void Outside_an_operation_a_note_goes_nowhere()
    {
        OutputNote.Add("Nobody is listening.");
    }

    [Fact]
    public void The_notification_for_one_output_adds_its_note()
    {
        var summary = BatchSummary.Describe([new FileResult("a.pdf", "C:/x/a (rotated).pdf", null) { Note = "Signed PDF: the digital signature is not valid in the output." }], 1, LogDir);

        Assert.Equal("Done: a (rotated).pdf. Signed PDF: the digital signature is not valid in the output.", summary.Text);
        Assert.False(summary.HasFailures);
    }

    [Fact]
    public void The_notification_for_several_outputs_counts_the_notes_and_points_to_the_log()
    {
        FileResult[] results =
        [
            new("a", "a.out", null) { Note = "Signed." },
            new("b", "b.out", null),
            new("c", "c.out", null) { Note = "Signed." },
        ];

        var summary = BatchSummary.Describe(results, 3, LogDir);

        Assert.Equal(@"Done: 3 files, 2 with a note. Details: D:\logs", summary.Text);
        Assert.True(summary.ShowsLog);
        Assert.False(summary.HasFailures);
    }

    [Fact]
    public void Notes_and_failures_share_the_pointer_to_the_log()
    {
        FileResult[] results = [new("a", "a.out", null) { Note = "Signed." }, new("b", null, "boom")];

        var summary = BatchSummary.Describe(results, 2, LogDir);

        Assert.Equal(@"Done: 1 file, 1 with a note, 1 failed. Details: D:\logs", summary.Text);
        Assert.True(summary.HasFailures);
    }

    private sealed class NotingCopy : IOperation
    {
        public string Id => "dev.note";

        public string MenuPath => "Note";

        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public int MaxParallel => 4;

        public async Task<string> RunAsync(string inputPath, CancellationToken ct)
        {
            var number = int.Parse(Path.GetFileNameWithoutExtension(inputPath)[4..], System.Globalization.CultureInfo.InvariantCulture);
            // Interleave with the other files, and note from another thread than the one that started.
            await Task.Delay(10 * (8 - number), ct);
            await Task.Run(() =>
            {
                if (number % 2 == 0)
                    OutputNote.Add($"Even file {number}.");
            }, ct);
            var output = inputPath + ".out";
            await File.WriteAllTextAsync(output, "x", ct);
            return output;
        }
    }

    private sealed class NotingJoin : IBatchOperation
    {
        public string Id => "dev.note-join";

        public string MenuPath => "Note join";

        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

        public async Task<string> RunAsync(IReadOnlyList<string> inputPaths, IProgress<FileProgress>? progress, CancellationToken ct)
        {
            OutputNote.Add($"Joined {inputPaths.Count} files.");
            var output = inputPaths[0] + ".joined";
            await File.WriteAllTextAsync(output, "x", ct);
            return output;
        }
    }
}
