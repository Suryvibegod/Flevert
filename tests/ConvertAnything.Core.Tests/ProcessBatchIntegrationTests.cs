using System.Diagnostics;

namespace ConvertAnything.Core.Tests;

/// <summary>Runs the real ca.exe as separate processes, the way Explorer does, with UI disabled.</summary>
[Collection(nameof(SequentialBatchTests))]
public sealed class ProcessBatchIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    // A late process must start within the leader's idle window; a full test run can starve it of CPU for longer than
    // the product's 400 ms (ADR-0002 accepts the resulting split). Tests that need one batch widen the window.
    private string? _idleWindowMs;

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

        // Headless: the leader exits as soon as the batch is done instead of waiting for a notification (which would add
        // at least the notification's lifetime). About 2 s alone; 4.1 s was seen while the image suite ran in parallel.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8), $"took {timer.Elapsed}");
        Assert.Equal(20, Directory.GetFiles(_dir.Path, "* (copy).txt").Length);
        Assert.Single(LoggedProcessIds());
    }

    [Fact]
    public async Task Process_started_while_the_leader_is_busy_joins_its_batch()
    {
        _idleWindowMs = "3000";
        var leader = Ca("dev.slow-copy", _dir.File("1.txt"), _dir.File("2.txt"));
        await Task.Delay(TimeSpan.FromSeconds(1.2)); // while file 2 is still running (the unit test in BatchTests owns the busy-leader rule)

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
        _idleWindowMs = "3000";
        var leader = CaVia(viaLink, "dev.slow-copy", _dir.File("1.txt"), _dir.File("2.txt"));
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var late = CaVia(viaLink, "dev.slow-copy", _dir.File("3.txt"));

        // Has failed under heavy parallel load (the image suite encoding at the same time): two batches after ~11 s, once
        // with an empty log. Any failed assertion below carries everything needed to tell why.
        try
        {
            await ExitAllOk([leader, late]);
            Assert.Equal(3, Directory.GetFiles(_dir.Path, "* (copy).txt").Length);
            Assert.Single(LoggedProcessIds());
        }
        catch (Exception failure)
        {
            // Also a TimeoutException from a hang; ExitAllOk has killed stragglers by then (exit -1).
            throw new Xunit.Sdk.XunitException(failure.Message + "\n" + Diagnostics(leader, late), failure);
        }
    }

    [Fact]
    public async Task A_running_ca_holds_the_app_mutex_the_installer_waits_for()
    {
        // Without it, uninstalling during a batch left ca.exe and its DLLs behind with no uninstaller to remove them.
        var job = Ca("dev.slow-copy", _dir.File("1.txt"), _dir.File("2.txt"));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(0.8));
            Assert.False(job.HasExited);
            Assert.True(Mutex.TryOpenExisting(AppMutex.Name, out var mutex));
            mutex.Dispose();
        }
        finally
        {
            await ExitAllOk([job]);
        }
    }

    [Fact]
    public async Task Install_menu_through_a_junction_is_refused_with_its_own_exit_code_and_reason()
    {
        using var links = new TempDir();
        var link = Path.Combine(links.Path, "app");
        await MakeJunction(link, Path.GetDirectoryName(CaExe)!);

        var start = new ProcessStartInfo(Path.Combine(link, "ca.exe"), "install-menu") { UseShellExecute = false, RedirectStandardError = true };
        using var ca = Process.Start(start)!;
        var stderr = await ca.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await ca.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(3, ca.ExitCode);
        Assert.Contains("other accounts can modify its folder", stderr, StringComparison.Ordinal);
    }

    private static async Task MakeJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        await mklink.WaitForExitAsync();
        Assert.Equal(0, mklink.ExitCode);
    }

    private string Diagnostics(params Process[] processes) =>
        string.Join(", ", processes.Select(p => $"pid {p.Id} " + (p.HasExited ? $"exit {p.ExitCode}" : "running"))) + "\n"
        + $"logs: {string.Join(", ", Directory.GetFiles(_logs.Path).Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} B)"))}\n"
        + $"outputs: {string.Join(", ", Directory.GetFiles(_dir.Path).Select(Path.GetFileName))}\n"
        + string.Join('\n', Directory.GetFiles(_logs.Path).SelectMany(File.ReadAllLines));

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

    [SkippableFact]
    public async Task A_short_8dot3_path_gives_an_output_named_after_the_long_name()
    {
        // Explorer passes 8.3 names for long paths; the output is named from the pinned file's real path.
        var full = _dir.File("Ảnh Đà Lạt rất dài tên.txt");
        var buffer = new char[1024];
        var length = GetShortPathNameW(full, buffer, (uint)buffer.Length);
        var shortPath = new string(buffer, 0, (int)length);
        Skip.If(length == 0 || shortPath == full, "8.3 short names are disabled on this volume.");

        using var ca = Ca("dev.copy", shortPath);
        await ExitAllOk([ca]);

        Assert.True(File.Exists(_dir.Combine("Ảnh Đà Lạt rất dài tên (copy).txt")), string.Join(", ", Directory.GetFiles(_dir.Path)));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [System.Runtime.InteropServices.Out] char[] shortPath, uint length);

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
        if (_idleWindowMs is not null)
            start.Environment["CONVERTANYTHING_BATCH_IDLE_MS"] = _idleWindowMs;
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
