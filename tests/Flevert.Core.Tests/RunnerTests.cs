using Flevert.Core.Operations;

namespace Flevert.Core.Tests;

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

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path, "flevert-*.log")));
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

    [WindowsTheory]
    [SupportedOSPlatform("windows")]
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

    // Opening a pipe as a file connects to its server, which may then impersonate the user.
    [WindowsTheory]
    [SupportedOSPlatform("windows")]
    [InlineData(@"\\localhost\pipe\x.txt")]
    [InlineData(@"\\?\UNC\localhost\PIPE\x.txt")]
    [InlineData("//localhost/pipe/x.txt")]
    [InlineData(@"\\;LanmanRedirector\localhost\pipe\x.txt")] // the redirector's own name, which shifts the share
    [InlineData(@"\\?\UNC\;LanmanRedirector\localhost\IPC$\x.txt")]
    public async Task Runner_rejects_named_pipe_paths(string path)
    {
        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [path], progress: null, CancellationToken.None);

        Assert.Contains("absolute", Assert.Single(outcome.Results).Error, StringComparison.Ordinal);
    }

    // Win32 strips a trailing dot or space, so "a.txt." names another file than the one the user clicked.
    [WindowsTheory]
    [SupportedOSPlatform("windows")]
    [InlineData("a.txt.")]
    [InlineData("a.txt ")]
    public async Task Runner_rejects_names_ending_in_a_dot_or_space(string name)
    {
        _dir.File("a.txt");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [_dir.Path + Path.DirectorySeparatorChar + name], progress: null, CancellationToken.None);

        Assert.Contains("dot or space", Assert.Single(outcome.Results).Error, StringComparison.Ordinal);
        Assert.False(File.Exists(_dir.Combine("a (copy).txt")));
    }

    // Win32 drops trailing dots from every folder in a path, so "sub.\x.txt" would be read from "sub\x.txt".
    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Runner_rejects_a_folder_name_ending_in_a_dot()
    {
        var real = Directory.CreateDirectory(_dir.Combine("sub")).FullName;
        File.WriteAllText(Path.Combine(real, "x.txt"), "protected");
        var decoy = Directory.CreateDirectory(@"\\?\" + _dir.Combine("sub.")).FullName;
        File.WriteAllText(Path.Combine(decoy, "x.txt"), "decoy");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [_dir.Path + @"\sub.\x.txt"], progress: null, CancellationToken.None);

        Assert.Contains("dot or space", Assert.Single(outcome.Results).Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(real, "x (copy).txt")));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Runner_rejects_an_alternate_data_stream()
    {
        // "a.txt:s.txt" passes the extension check but names a hidden stream of a.txt, not a file.
        var file = _dir.File("a.txt", "visible");
        File.WriteAllText(file + ":s.txt", "hidden");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [file + ":s.txt"], progress: null, CancellationToken.None);

        Assert.Contains("stream", Assert.Single(outcome.Results).Error, StringComparison.Ordinal);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void The_pin_refuses_a_real_location_with_a_folder_ending_in_a_dot()
    {
        // A junction can lead a clean-looking path into "x."; the pin then sees "x." in the real path. The operation
        // reopens that path, and anything that normalises it would read "x\a.txt", which was never pinned or checked.
        // (mklink normalises its target, so the test reaches "x." through \\?\, which the pin opens verbatim.)
        var dotted = Directory.CreateDirectory(@"\\?\" + _dir.Combine("x.")).FullName;
        File.WriteAllText(Path.Combine(dotted, "a.txt"), "pinned");

        using var pin = InputPin.TryPin(Path.Combine(dotted, "a.txt"), out var reason, out _);

        Assert.Null(pin);
        Assert.Contains("dot or space", reason, StringComparison.Ordinal);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void The_pin_refuses_anything_that_is_not_a_file_on_disk()
    {
        // Every pipe spelling is refused earlier; this is the last line if one ever gets through.
        var name = $"flevert-test-{Guid.NewGuid():N}";
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        _ = server.WaitForConnectionAsync();

        using var pin = InputPin.TryPin($@"\\?\pipe\{name}", out var reason, out _);

        Assert.Null(pin);
        Assert.Contains("on disk", reason, StringComparison.Ordinal);
    }

    // The \\?\ prefix is dropped only where Win32 normalisation would leave the path unchanged.
    [WindowsTheory]
    [SupportedOSPlatform("windows")]
    [InlineData(@"\\?\C:\a\b.txt", @"C:\a\b.txt")]
    [InlineData(@"\\?\UNC\server\share\x.txt", @"\\server\share\x.txt")]
    [InlineData(@"\\?\C:\a\NUL", @"\\?\C:\a\NUL")]
    [InlineData(@"\\?\C:\a\b.txt ", @"\\?\C:\a\b.txt ")]
    [InlineData(@"\\?\Volume{0a1b2c3d-0000-0000-0000-000000000000}\x.txt", @"\\?\Volume{0a1b2c3d-0000-0000-0000-000000000000}\x.txt")]
    public void The_real_path_keeps_its_prefix_when_dropping_it_would_name_something_else(string final, string expected)
    {
        Assert.Equal(expected, InputPin.Shorten(final));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task A_relative_path_through_a_dotted_folder_is_refused_for_the_dot()
    {
        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [@"sub.\x.txt"], progress: null, CancellationToken.None);

        Assert.Contains("dot or space", Assert.Single(outcome.Results).Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\a\sub.\x.txt", true)]
    [InlineData(@"C:\a\sub \x.txt", true)]
    [InlineData(@"\\?\C:\a\x.\y.txt", true)]
    [InlineData(@"\\server\share.\x.txt", true)]
    [InlineData(@"..\x.txt", false)]
    [InlineData(@".\dir\x.txt", false)]
    [InlineData(@"C:\a\.hidden\x.txt", false)]
    [InlineData(@"\\?\UNC\server\share\x.txt", false)]
    [InlineData(@"\??\C:\a\x.\y.txt", true)]
    public void Names_ending_in_a_dot_or_space_are_found_in_any_part_of_a_path(string path, bool expected)
    {
        Assert.Equal(expected, InputPath.HasNameEndingInDotOrSpace(path));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task A_short_name_with_a_truncated_extension_is_judged_by_the_real_name()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only");
        // 8.3 names cut 4-letter extensions (.webp -> .WEB, .jpeg -> .JPE, .heic -> .HEI). Normalisation expands them, but
        // not for \\?\ paths, which reach the runner as given.
        var file = _dir.File("averylongname.webp");
        var buffer = new char[1024];
        var length = GetShortPathNameW(file, buffer, (uint)buffer.Length);
        var shortPath = new string(buffer, 0, (int)length);
        Skip.If(length == 0 || shortPath == file, "this volume does not create 8.3 names");
        var op = new WebpOp();

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(op, [@"\\?\" + shortPath], progress: null, CancellationToken.None);

        Assert.True(Assert.Single(outcome.Results).Succeeded, outcome.Results[0].Error);
        Assert.EndsWith("averylongname.webp", op.Received, StringComparison.Ordinal);
    }

    private sealed class WebpOp : IOperation
    {
        public string? Received { get; private set; }
        public string Id => "test.webp";
        public string MenuPath => "Test/WebP";
        public IReadOnlyCollection<string> Extensions { get; } = [".webp"];
        public Task<string> RunAsync(string inputPath, CancellationToken ct)
        {
            Received = inputPath;
            return Task.FromResult(inputPath);
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Runner_checks_the_file_type_of_the_real_name_not_of_a_short_name()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows only");
        var file = _dir.File("abcdefghij.txtx");
        var buffer = new char[1024];
        var length = GetShortPathNameW(file, buffer, (uint)buffer.Length);
        var shortPath = new string(buffer, 0, (int)length);
        Skip.If(length == 0 || shortPath == file, "this volume does not create 8.3 names");
        Assert.EndsWith(".TXT", shortPath, StringComparison.Ordinal);

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new DevCopy(), [shortPath], progress: null, CancellationToken.None);

        Assert.Contains("not supported", Assert.Single(outcome.Results).Error, StringComparison.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [System.Runtime.InteropServices.Out] char[] shortPath, uint length);

    [Fact]
    public async Task An_operation_can_skip_a_file_with_a_reason_without_failing()
    {
        var file = _dir.File("a.txt");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new SkippingOp(), [file], progress: null, CancellationToken.None);

        var result = Assert.Single(outcome.Results);
        Assert.True(result.Skipped);
        Assert.False(result.Failed);
        Assert.Equal("nothing to gain", result.Error);
        var line = Assert.Single(File.ReadAllLines(Directory.GetFiles(_logs.Path).Single())).Split('\t');
        Assert.Equal("skipped", line[4]);
        Assert.Equal("nothing to gain", line[6]); // the reason, not a stack trace
    }

    [Fact]
    public async Task A_skip_without_a_reason_still_says_something()
    {
        var file = _dir.File("a.txt");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new SkippingOp(" "), [file], progress: null, CancellationToken.None);

        Assert.Equal("Nothing to gain.", Assert.Single(outcome.Results).Error);
    }

    [Fact]
    public async Task A_failed_file_carries_the_reason_on_its_first_line_and_the_stack_after_it()
    {
        // The notification shows the first line (BatchSummary); the log keeps the rest.
        var file = _dir.File("a.txt");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(
            new FailingOp(), [file], progress: null, CancellationToken.None);

        var error = Assert.Single(outcome.Results).Error!;
        Assert.StartsWith("The file is damaged." + Environment.NewLine, error, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidDataException), error, StringComparison.Ordinal);
        Assert.Contains(nameof(FailingOp), error, StringComparison.Ordinal);
    }

    private sealed class FailingOp : IOperation
    {
        public string Id => "test.fail";
        public string MenuPath => "Test/Fail";
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public Task<string> RunAsync(string inputPath, CancellationToken ct) => throw new InvalidDataException("The file is damaged.");
    }

    private sealed class SkippingOp(string reason = "nothing to gain") : IOperation
    {
        public string Id => "test.skip";
        public string MenuPath => "Test/Skip";
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public Task<string> RunAsync(string inputPath, CancellationToken ct) => throw new OperationSkippedException(reason);
    }

    [Fact]
    public async Task Cancel_returns_promptly_even_if_the_operation_ignores_the_token()
    {
        var file = _dir.File("a.txt");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var timer = System.Diagnostics.Stopwatch.StartNew();

        await new Runner(new RunLog(_logs.Path)).RunAsync(new StubbornOp(), [file], progress: null, cts.Token);

        // The two seconds it gets to stop, then it is abandoned.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), $"took {timer.Elapsed}");
    }

    [Fact]
    public async Task A_cancelled_operation_gets_time_to_remove_what_it_wrote_before_the_batch_ends()
    {
        // flevert may end right after the batch: a .partial left by an operation still stopping would stay (seen with a
        // PDF to images cancelled from the progress window on Linux).
        var file = _dir.File("a.txt");
        var op = new SlowToStopOp();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<BatchProgress>(_ => { if (op.Wrote is not null) cts.Cancel(); });

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(op, [file], progress, cts.Token);

        Assert.Empty(outcome.Results);
        Assert.NotNull(op.Wrote);
        Assert.False(File.Exists(op.Wrote));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task An_abandoned_operation_still_reads_the_file_it_was_given_through_its_pin()
    {
        var file = _dir.File("a.txt", "the input");
        var op = new LateReaderOp();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await new Runner(new RunLog(_logs.Path)).RunAsync(op, [file], progress: null, cts.Token);

        Assert.Equal("the input", await op.Read.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Input_cannot_be_renamed_or_replaced_while_it_is_being_processed()
    {
        var file = _dir.File("a.txt");
        var op = new RenamingOp();

        await new Runner(new RunLog(_logs.Path)).RunAsync(op, [file], progress: null, CancellationToken.None);

        Assert.False(op.RenameSucceeded);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
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

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void A_parent_folder_cannot_be_renamed_while_its_file_is_pinned()
    {
        // The operation is given the real path on the strength of this: with the input open without delete sharing,
        // nobody can move a parent folder away and put a junction in its place.
        var parent = Directory.CreateDirectory(_dir.Combine("sub")).FullName;
        var file = Path.Combine(parent, "a.txt");
        File.WriteAllText(file, "x");

        using (var pin = InputPin.TryPin(file, out var reason, out _))
        {
            Assert.True(pin is not null, reason);
            Assert.ThrowsAny<IOException>(() => Directory.Move(parent, _dir.Combine("moved")));
        }

        Directory.Move(parent, _dir.Combine("moved")); // control: possible once the pin is released
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
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

    [SupportedOSPlatform("windows")]
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

    // Writes a temporary file, then takes half a second to remove it once cancelled.
    private sealed class SlowToStopOp : IOperation
    {
        public string Id => "test.slow-to-stop";
        public string MenuPath => "Test/Slow to stop";
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public string? Wrote { get; private set; }

        public async Task<string> RunAsync(string inputPath, CancellationToken ct) => await RunAsync(inputPath, null, ct);

        public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
        {
            var temp = inputPath + ".partial";
            await File.WriteAllTextAsync(temp, "half", CancellationToken.None);
            Wrote = temp;
            progress?.Report(new FileProgress("step", 1, 2));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return inputPath;
            }
            finally
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
                File.Delete(temp);
            }
        }
    }

    // Ignores the token, and reads its input through its pin long after the runner gave up on it.
    private sealed class LateReaderOp : IOperation
    {
        public string Id => "test.late-reader";
        public string MenuPath => "Test/Late reader";
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public TaskCompletionSource<string> Read { get; } = new();

        public async Task<string> RunAsync(string inputPath, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
            try
            {
                Read.SetResult(await File.ReadAllTextAsync(PinnedInput.ReadPath(inputPath), CancellationToken.None));
            }
            catch (IOException e)
            {
                Read.SetResult(e.Message);
            }

            return inputPath;
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
