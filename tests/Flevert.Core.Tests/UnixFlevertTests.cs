using System.Diagnostics;

namespace Flevert.Core.Tests;

/// <summary>
/// flevert on Linux, run as Nautilus runs it: once, with every selected file (docs/specs/linux.md). Headless here; the
/// progress window and notifications come with the desktop.
/// </summary>
public sealed class UnixFlevertTests : IDisposable
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
    public void Every_file_given_at_once_is_processed_in_one_run()
    {
        var files = new[] { _dir.File("ảnh 1.txt"), _dir.File("ảnh 2.txt"), _dir.File("ảnh 10.txt") };

        var (exitCode, error) = Run(["run", "dev.copy", .. files]);

        Assert.True(exitCode == ExitCodes.Ok, error);
        Assert.Equal(["ảnh 1 (copy).txt", "ảnh 1.txt", "ảnh 10 (copy).txt", "ảnh 10.txt", "ảnh 2 (copy).txt", "ảnh 2.txt"], _dir.FileNames());
        Assert.Equal(3, LogLines().Length);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_file_that_fails_makes_the_run_fail_but_not_the_others()
    {
        var good = _dir.File("a.txt");

        var (exitCode, _) = Run(["run", "dev.copy", good, _dir.Combine("missing.txt")]);

        Assert.Equal(ExitCodes.Failed, exitCode);
        Assert.True(File.Exists(_dir.Combine("a (copy).txt")));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_relative_path_is_taken_from_the_folder_flevert_was_started_in()
    {
        _dir.File("a.txt");

        var (exitCode, error) = Run(["run", "dev.copy", "a.txt"], workingDirectory: _dir.Path);

        Assert.True(exitCode == ExitCodes.Ok, error);
        Assert.True(File.Exists(_dir.Combine("a (copy).txt")));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void On_a_new_account_the_log_goes_where_the_data_folder_will_be_never_in_the_folder_flevert_started_in()
    {
        // ~/.local/share may not exist yet: .NET then gives "" for it, unless told not to check.
        var data = _dir.Combine("not made yet");
        var start = Start(["run", "dev.copy", _dir.File("a.txt")]);
        start.Environment.Remove("FLEVERT_LOG_DIR");
        start.Environment["XDG_DATA_HOME"] = data;
        using var startedIn = new TempDir();
        start.WorkingDirectory = startedIn.Path;

        using (var flevert = Process.Start(start)!)
        {
            flevert.StandardOutput.ReadToEnd();
            Assert.True(flevert.WaitForExit(TimeSpan.FromMinutes(2)), "flevert did not finish");
            Assert.Equal(ExitCodes.Ok, flevert.ExitCode);
        }

        Assert.Single(Directory.GetFiles(Path.Combine(data, "Flevert", "logs")));
        Assert.Empty(Directory.GetFileSystemEntries(startedIn.Path));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void An_unknown_operation_is_bad_usage()
    {
        var (exitCode, _) = Run(["run", "no.such-op", _dir.File("a.txt")]);

        Assert.Equal(ExitCodes.BadUsage, exitCode);
        Assert.Equal(["a.txt"], _dir.FileNames());
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void More_files_than_a_batch_takes_are_refused_and_nothing_is_written()
    {
        var files = Enumerable.Range(1, 1001).Select(i => _dir.File($"{i}.txt")).ToArray();

        var (exitCode, error) = Run(["run", "dev.copy", .. files]);

        Assert.Equal(ExitCodes.BadUsage, exitCode);
        Assert.Contains("1000", error, StringComparison.Ordinal);
        Assert.Equal(1001, _dir.FileNames().Length);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void List_ops_names_every_operation_of_the_menu()
    {
        var start = Start(["list-ops"]);
        using var flevert = Process.Start(start)!;
        var output = flevert.StandardOutput.ReadToEnd();
        flevert.WaitForExit();

        Assert.Equal(0, flevert.ExitCode);
        Assert.Contains("image.to-jpg\tConvert to/JPG\t", output, StringComparison.Ordinal);
        Assert.Contains("pdf.merge\t", output, StringComparison.Ordinal);
    }

    private (int ExitCode, string Error) Run(string[] arguments, string? workingDirectory = null)
    {
        var start = Start(arguments);
        start.WorkingDirectory = workingDirectory ?? _dir.Path;
        using var flevert = Process.Start(start)!;
        var error = flevert.StandardError.ReadToEndAsync();
        _ = flevert.StandardOutput.ReadToEnd();
        Assert.True(flevert.WaitForExit(TimeSpan.FromMinutes(2)), "flevert did not finish");
        return (flevert.ExitCode, error.Result);
    }

    private ProcessStartInfo Start(string[] arguments)
    {
        var start = new ProcessStartInfo(BuiltFlevert.Path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment["FLEVERT_HEADLESS"] = "1";
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        return start;
    }

    private string[] LogLines() =>
        File.ReadAllLines(Assert.Single(Directory.GetFiles(_logs.Path, "flevert-*.log")));
}
