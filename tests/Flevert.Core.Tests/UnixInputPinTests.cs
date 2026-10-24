using System.Diagnostics;
using Flevert.Core.Operations;

namespace Flevert.Core.Tests;

/// <summary>
/// On Linux an input is opened without following a link and checked before anything reads it: only a regular file with a
/// single name is processed, and the operation is given its real location (docs/specs/linux.md).
/// </summary>
public sealed class UnixInputPinTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_message_names_a_pinned_input_as_the_user_knows_it_and_nothing_else()
    {
        var file = _dir.File("Hồ sơ $1.pdf");
        using var pin = Libc.Open(file, Libc.OPath | Libc.OCloexec);
        PinnedInput.Use([(file, pin)]);
        var handle = PinnedInput.ReadPath(file);

        var shown = PinnedInput.ShowNames($"{handle}: denied; {handle}0/page.png: full");

        // "$1" is no group reference, and the number of another handle is left alone.
        Assert.Equal($"{file}: denied; {handle}0/page.png: full", shown);
        Assert.StartsWith("/proc/", handle, StringComparison.Ordinal);
        Assert.Equal(new FileInfo(file).Length, PinnedInput.Length(file));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_plain_file_is_pinned_at_its_real_path()
    {
        var file = _dir.File("a.txt");

        using var pin = UnixInputPin.TryPin(file, out var reason, out var realPath);

        Assert.True(pin is not null, reason);
        Assert.Equal(file, realPath);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_symbolic_link_to_a_file_is_refused()
    {
        var target = _dir.File("private.txt", "secret");
        var link = _dir.Combine("shared.txt");
        File.CreateSymbolicLink(link, target);

        using var pin = UnixInputPin.TryPin(link, out var reason, out _);

        Assert.Null(pin);
        Assert.Contains("Symbolic links", reason, StringComparison.Ordinal);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_file_with_more_than_one_hard_link_is_refused()
    {
        var target = _dir.File("private.txt", "secret");
        var link = _dir.Combine("shared.txt");
        Run("ln", target, link);

        using var pin = UnixInputPin.TryPin(link, out var reason, out _);

        Assert.Null(pin);
        Assert.Contains("hard link", reason, StringComparison.Ordinal);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_fifo_is_refused_at_once_without_waiting_for_a_writer()
    {
        var fifo = _dir.Combine("pipe.txt");
        Run("mkfifo", fifo);
        var timer = Stopwatch.StartNew();

        using var pin = UnixInputPin.TryPin(fifo, out var reason, out _);

        Assert.Null(pin);
        Assert.Contains("on disk", reason, StringComparison.Ordinal);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"took {timer.Elapsed}");
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_device_is_refused()
    {
        using var pin = UnixInputPin.TryPin("/dev/null", out var reason, out _);

        Assert.Null(pin);
        Assert.Contains("on disk", reason, StringComparison.Ordinal);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_folder_is_refused()
    {
        var folder = Directory.CreateDirectory(_dir.Combine("sub.txt")).FullName;

        using var pin = UnixInputPin.TryPin(folder, out var reason, out _);

        Assert.Null(pin);
        Assert.Contains("on disk", reason, StringComparison.Ordinal);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_missing_file_is_refused_with_the_reason_the_system_gives()
    {
        using var pin = UnixInputPin.TryPin(_dir.Combine("missing.txt"), out var reason, out _);

        Assert.Null(pin);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_operation_gets_the_real_location_when_a_parent_folder_is_a_symbolic_link()
    {
        var real = Directory.CreateDirectory(_dir.Combine("real")).FullName;
        File.WriteAllText(Path.Combine(real, "a.txt"), "x");
        var via = _dir.Combine("via");
        Directory.CreateSymbolicLink(via, real);
        var op = new RecordingOp();

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(op, [Path.Combine(via, "a.txt")], progress: null, CancellationToken.None);

        Assert.True(Assert.Single(outcome.Results).Succeeded, outcome.Results[0].Error);
        Assert.Equal(Path.Combine(real, "a.txt"), op.Received);
    }

    [LinuxTheory]
    [SupportedOSPlatform("linux")]
    [InlineData("sub.", "a.txt")] // Windows drops a trailing dot; Linux keeps it, so it names this folder and no other
    [InlineData("sub ", "a.txt")]
    [InlineData("sub", "a:b.txt")] // a colon is an alternate data stream on NTFS, a plain character here
    public async Task Names_that_are_ordinary_on_linux_are_processed(string folder, string name)
    {
        var dir = Directory.CreateDirectory(_dir.Combine(folder)).FullName;
        var file = Path.Combine(dir, name);
        File.WriteAllText(file, "x");

        var outcome = await new Runner(new RunLog(_logs.Path)).RunAsync(new DevCopy(), [file], progress: null, CancellationToken.None);

        Assert.True(Assert.Single(outcome.Results).Succeeded, outcome.Results[0].Error);
        Assert.True(File.Exists(Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (copy).txt")));
    }

    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

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
}
