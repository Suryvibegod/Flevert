using System.Diagnostics;

namespace ConvertAnything.Core.Tests;

/// <summary>Runs the real ca.exe as separate processes, the way Explorer does, with UI disabled.</summary>
[Collection(nameof(SequentialBatchTests))]
public sealed class ProcessBatchIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Twenty_processes_started_back_to_back_form_one_batch()
    {
        var files = Enumerable.Range(1, 20).Select(i => _dir.File($"ảnh {i}.txt")).ToList();
        var timer = Stopwatch.StartNew();

        var processes = files.Select(f => Ca("dev.copy", f)).ToList();
        await ExitAllOk(processes);

        // Headless: the leader exits as soon as the batch is done instead of waiting for a notification.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), $"took {timer.Elapsed}");
        Assert.Equal(20, Directory.GetFiles(_dir.Path, "* (copy).txt").Length);
        Assert.Single(LoggedProcessIds());
    }

    [Fact]
    public async Task Process_started_while_the_leader_is_busy_joins_its_batch()
    {
        var leader = Ca("dev.slow-copy", _dir.File("1.txt"), _dir.File("2.txt"));
        await Task.Delay(TimeSpan.FromSeconds(1.2)); // well past the 400 ms idle window, while file 2 is still running

        var late = Ca("dev.slow-copy", _dir.File("3.txt"));
        await ExitAllOk([leader, late]);

        Assert.Equal(3, Directory.GetFiles(_dir.Path, "* (copy).txt").Length);
        Assert.Equal([leader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)], LoggedProcessIds());
    }

    [Fact]
    public async Task Processes_started_through_a_junction_still_form_one_batch()
    {
        // Peers are matched by executable path; the kernel reports the resolved path, the process its launch path.
        using var links = new TempDir();
        var link = Path.Combine(links.Path, "app");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.GetDirectoryName(CaExe)}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!)
        {
            await mklink.WaitForExitAsync();
            Assert.Equal(0, mklink.ExitCode);
        }

        var viaLink = Path.Combine(link, "ca.exe");
        var leader = CaVia(viaLink, "dev.slow-copy", _dir.File("1.txt"), _dir.File("2.txt"));
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var late = CaVia(viaLink, "dev.slow-copy", _dir.File("3.txt"));
        await ExitAllOk([leader, late]);

        Assert.Equal(3, Directory.GetFiles(_dir.Path, "* (copy).txt").Length);
        // Failed once in six full-suite runs (two batches after ~11 s); the log shows which process did what.
        Assert.True(LoggedProcessIds().Length == 1,
            $"leader {leader.Id}, late {late.Id}:\n" + string.Join('\n', Directory.GetFiles(_logs.Path).SelectMany(File.ReadAllLines)));
    }

    [Fact]
    public async Task A_name_ending_in_a_dot_is_not_resolved_to_the_file_without_it()
    {
        // A decoy "budget.txt." beside a protected budget.txt must not make ca convert budget.txt.
        _dir.File("budget.txt");

        using var ca = Ca("dev.copy", _dir.Combine("budget.txt."));
        await ca.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(File.Exists(_dir.Combine("budget (copy).txt")));
    }

    [Fact]
    public async Task A_folder_name_ending_in_a_dot_is_not_resolved_to_the_folder_without_it()
    {
        var real = Directory.CreateDirectory(_dir.Combine("sub")).FullName;
        File.WriteAllText(Path.Combine(real, "x.txt"), "protected");
        var decoy = Directory.CreateDirectory(@"\\?\" + _dir.Combine("sub.")).FullName;
        File.WriteAllText(Path.Combine(decoy, "x.txt"), "decoy");

        using var ca = Ca("dev.copy", _dir.Path + @"\sub.\x.txt");
        await ca.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(File.Exists(Path.Combine(real, "x (copy).txt")));
    }

    [Fact]
    public async Task Relative_paths_resolve_against_the_directory_ca_was_started_in()
    {
        _dir.File("relative.txt");

        await ExitAllOk([Ca("dev.copy", "relative.txt")]);

        Assert.True(File.Exists(_dir.Combine("relative (copy).txt")));
    }

    private Process Ca(string operation, params string[] files) => CaVia(CaExe, operation, files);

    private Process CaVia(string exe, string operation, params string[] files)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = _dir.Path };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(operation);
        foreach (var f in files)
            start.ArgumentList.Add(f);
        start.Environment["CONVERTANYTHING_LOG_DIR"] = _logs.Path;
        start.Environment["CONVERTANYTHING_HEADLESS"] = "1";
        return Process.Start(start)!;
    }

    /// <summary>Every process must exit with 0 within 30 s; stragglers are killed so a hang fails instead of stalling.</summary>
    private static async Task ExitAllOk(IReadOnlyList<Process> processes)
    {
        try
        {
            await Task.WhenAll(processes.Select(p => p.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(processes, p => Assert.Equal(0, p.ExitCode));
        }
        finally
        {
            foreach (var p in processes.Where(p => !p.HasExited))
                p.Kill(entireProcessTree: true);
        }
    }

    private string[] LoggedProcessIds() =>
        Directory.GetFiles(_logs.Path, "ca-*.log").SelectMany(File.ReadAllLines)
            .Select(line => line.Split('\t')[1]).Distinct().ToArray();

    private static string CaExe
    {
        get
        {
            // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = output.Parent!.Name;
            var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            var exe = Path.Combine(root, "src", "ConvertAnything.Cli", "bin", configuration, output.Name, "ca.exe");
            Assert.True(File.Exists(exe), $"Build the solution first; missing {exe}");
            return exe;
        }
    }
}
