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

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [missing, good], progress: null, CancellationToken.None);

        Assert.Equal(2, outcome.Total);
        Assert.Collection(outcome.Results,
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

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [first, second], progress, cts.Token);

        Assert.Single(outcome.Results);
        Assert.Equal(2, outcome.Total);
        Assert.False(File.Exists(_dir.Combine("2 (copy).txt")));
    }

    [Fact]
    public async Task File_interrupted_by_cancel_is_not_reported_as_a_result()
    {
        var first = _dir.File("1.txt");
        var second = _dir.File("2.txt");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<BatchProgress>(p => { if (p.Current == second) cts.Cancel(); });

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevSlowCopy(), [first, second], progress, cts.Token);

        Assert.Equal([first], outcome.Results.Select(r => r.Input));
        Assert.Equal(2, outcome.Total);
        Assert.False(File.Exists(_dir.Combine("2 (copy).txt")));
    }

    [Fact]
    public async Task Runner_processes_a_path_received_twice_only_once()
    {
        var file = _dir.File("a.txt");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [file, file], progress: null, CancellationToken.None);

        Assert.Single(outcome.Results);
        Assert.Equal(["a (copy).txt", "a.txt"], _dir.FileNames());
    }

    [Fact]
    public async Task Runner_treats_paths_differing_only_in_case_as_distinct()
    {
        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [_dir.Combine("A.txt"), _dir.Combine("a.txt")], progress: null, CancellationToken.None);

        Assert.Equal(2, outcome.Results.Count);
    }

    [Fact]
    public async Task Runner_rejects_a_file_type_the_operation_does_not_support()
    {
        var image = _dir.File("photo.jpg");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [image], progress: null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.False(result.Succeeded);
        Assert.Contains("not supported", result.Error, StringComparison.Ordinal);
        Assert.Equal(["photo.jpg"], _dir.FileNames());
    }

    [SkippableFact]
    public async Task Runner_rejects_a_symbolic_link_instead_of_following_it()
    {
        var target = _dir.File("private.txt", "secret");
        var link = _dir.Combine("shared.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Skip.If(true, "Creating symbolic links needs Developer Mode or admin rights on this machine.");
        }

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [link], progress: null, CancellationToken.None);

        Assert.False(Assert.Single(outcome.Results).Succeeded);
        Assert.False(File.Exists(_dir.Combine("shared (copy).txt")));
    }

    [Theory]
    [InlineData("relative.txt")]
    [InlineData(@"\\.\C:\x.txt")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x.txt")]
    public async Task Runner_rejects_paths_that_are_not_plain_absolute_file_paths(string path)
    {
        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [path], progress: null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.False(result.Succeeded);
        Assert.Contains("absolute", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//./C:/x.txt")]
    [InlineData(@"\??\GLOBALROOT\Device\HarddiskVolume1\x.txt")]
    [InlineData("//?/GLOBALROOT/Device/HarddiskVolume1/x.txt")]
    [InlineData(@"\./C:\x.txt")]
    public async Task Runner_rejects_device_paths_however_they_are_spelled(string path)
    {
        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [path], progress: null, CancellationToken.None);

        Assert.Contains("absolute", Assert.Single(outcome.Results).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_returns_promptly_even_if_the_operation_ignores_the_token()
    {
        var file = _dir.File("a.txt");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await new Runner(new RunLog(_logs.Path)).RunAsync(new StubbornOp(), [file], progress: null, cts.Token);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"took {timer.Elapsed}");
    }

    [Fact]
    public async Task Input_cannot_be_renamed_or_replaced_while_it_is_being_processed()
    {
        var file = _dir.File("a.txt");
        var op = new RenamingOp();

        await new Runner(new RunLog(_logs.Path)).RunAsync(op, [file], progress: null, CancellationToken.None);

        Assert.False(op.RenameSucceeded);
    }

    [Fact]
    public async Task Operation_receives_the_real_location_when_the_path_goes_through_a_junction()
    {
        var real = Directory.CreateDirectory(_dir.Combine("real")).FullName;
        File.WriteAllText(Path.Combine(real, "a.txt"), "x");
        var junction = _dir.Combine("via");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{real}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        await mklink.WaitForExitAsync();
        Assert.Equal(0, mklink.ExitCode);
        var op = new RecordingOp();

        try
        {
            var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(op, [Path.Combine(junction, "a.txt")], progress: null, CancellationToken.None);

            Assert.True(Assert.Single(outcome.Results).Succeeded, outcome.Results[0].Error);
            // A junction swapped mid-run cannot redirect where the input is read or the output written.
            Assert.Equal(Path.Combine(real, "a.txt"), op.Received, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(junction); // removes the link only, never its target
        }
    }

    [Fact]
    public async Task Runner_rejects_a_file_with_more_than_one_hard_link()
    {
        var target = _dir.File("private.txt", "secret");
        var link = _dir.Combine("shared.txt");
        Assert.True(CreateHardLinkW(link, target, 0), "could not create a hard link");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [link], progress: null, CancellationToken.None);

        Assert.Contains("hard link", Assert.Single(outcome.Results).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_handles_inputs_longer_than_260_characters()
    {
        var deep = _dir.Path;
        while (deep.Length < 300)
            deep = Path.Combine(deep, new string('d', 40));
        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, "a.txt");
        File.WriteAllText(file, "deep");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [file], progress: null, CancellationToken.None);

        Assert.True(Assert.Single(outcome.Results).Succeeded, outcome.Results[0].Error);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string link, string existing, nint security);

    private sealed class RecordingOp : IOperation
    {
        public string? Received { get; private set; }
        public string Id => "test.record";
        public string MenuPath => "Test/Record";
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public Task<string> RunAsync(string inputPath, CancellationToken ct)
        {
            Received = inputPath;
            return Task.FromResult(inputPath);
        }
    }

    private sealed class StubbornOp : IOperation
    {
        public string Id => "test.stubborn";
        public string MenuPath => "Test/Stubborn";
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public async Task<string> RunAsync(string inputPath, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
            return inputPath;
        }
    }

    private sealed class RenamingOp : IOperation
    {
        public bool RenameSucceeded { get; private set; }
        public string Id => "test.rename";
        public string MenuPath => "Test/Rename";
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public Task<string> RunAsync(string inputPath, CancellationToken ct)
        {
            try
            {
                File.Move(inputPath, inputPath + ".moved");
                RenameSucceeded = true;
            }
            catch (IOException)
            {
            }

            return Task.FromResult(inputPath);
        }
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
