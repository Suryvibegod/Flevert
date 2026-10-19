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

    // How long dev.slow-copy takes per file (CONVERTANYTHING_SLOW_COPY_MS; 1 s by default).
    private string? _slowCopyMs;

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

    [SkippableFact]
    public async Task A_batch_converts_several_files_at_once_and_stays_one_batch()
    {
        Skip.If(Environment.ProcessorCount < 3, "ca.exe runs files in parallel only with three or more cores.");
        _idleWindowMs = "3000";
        var files = Enumerable.Range(1, 6).Select(i => _dir.File($"{i}.txt")).ToList();

        await ExitAllOk(files.Select(f => Ca("dev.slow-copy", f)).ToList());

        // Each log line is written when its file is done, with its duration: rebuild when each file ran.
        var runs = Directory.GetFiles(_logs.Path, "ca-*.log").SelectMany(File.ReadAllLines).Select(line =>
        {
            var fields = line.Split('\t');
            var end = DateTimeOffset.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
            return (Start: end - TimeSpan.FromMilliseconds(double.Parse(fields[5], System.Globalization.CultureInfo.InvariantCulture)), End: end);
        }).OrderBy(r => r.Start).ToList();
        Assert.Equal(6, runs.Count);
        Assert.Single(LoggedProcessIds());
        Assert.True(runs.Zip(runs.Skip(1)).Any(pair => pair.Second.Start < pair.First.End),
            "no two files ran at the same time: " + string.Join(", ", runs.Select(r => $"{r.Start:HH:mm:ss.fff}-{r.End:HH:mm:ss.fff}")));
    }

    [Fact]
    public async Task Process_started_while_the_leader_is_busy_joins_its_batch()
    {
        // The late process starts after both the idle window and the batch's minimum lifetime, while the leader's only
        // file is still converting: only ca.exe telling the leader that a file is running keeps the batch open (a
        // parallel runner has already asked for the next file).
        _idleWindowMs = "200";
        _slowCopyMs = "6000"; // room for the late process to start slowly; it must arrive while file 1 still runs
        var leader = Ca("dev.slow-copy", _dir.File("1.txt"));
        await Task.Delay(TimeSpan.FromSeconds(2.2));

        var late = Ca("dev.slow-copy", _dir.File("2.txt"));
        await ExitAllOk([leader, late]);

        Assert.Equal(2, Directory.GetFiles(_dir.Path, "* (copy).txt").Length);
        Assert.Equal([leader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)], LoggedProcessIds());
    }

    [SkippableTheory]
    [InlineData(2, ProcessPriorityClass.BelowNormal)] // several conversions at once must not make the PC sluggish
    [InlineData(1, ProcessPriorityClass.Normal)] // one alone must not be starved past ImageMagick's per-image time limit
    public async Task A_leader_lowers_its_priority_only_while_files_run_in_parallel(int files, ProcessPriorityClass expected)
    {
        Skip.If(Environment.ProcessorCount < 3, "ca.exe runs files in parallel only with three or more cores.");
        _slowCopyMs = "3000";
        var leader = Ca("dev.slow-copy", [.. Enumerable.Range(1, files).Select(i => _dir.File($"{i}.txt"))]);
        try
        {
            // A cold start can be slow on CI: watch for up to 2.5 s of the 3 s the files take.
            var seen = ProcessPriorityClass.Normal;
            for (var waited = 0; waited < 50 && seen != ProcessPriorityClass.BelowNormal && !leader.HasExited; waited++)
            {
                await Task.Delay(50);
                leader.Refresh();
                seen = leader.PriorityClass;
            }

            Assert.Equal(expected, seen);
        }
        finally
        {
            await ExitAllOk([leader]);
        }
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

    [SkippableFact]
    public async Task A_running_ca_holds_the_app_mutex_the_installer_waits_for()
    {
        // Without it, uninstalling during a batch left ca.exe and its DLLs behind with no uninstaller to remove them.
        // Another suite may run ca.exe at the same time; only a mutex that appears with this job proves anything.
        // Dispose what was opened: a handle kept by this process would keep the name alive and skip the next test too.
        var othersRunning = Mutex.TryOpenExisting(AppMutex.Name, out var before);
        before?.Dispose();
        Skip.If(othersRunning, "Another ca.exe is running.");
        var job = Ca("dev.slow-copy", _dir.File("1.txt"), _dir.File("2.txt"));
        try
        {
            // The first run of a fresh build can be slow to start (Smart App Control checks new binaries).
            Mutex? mutex = null;
            for (var waited = 0; waited < 50 && !job.HasExited && !Mutex.TryOpenExisting(AppMutex.Name, out mutex); waited++)
                await Task.Delay(100);
            Assert.False(job.HasExited);
            Assert.NotNull(mutex);
            mutex.Dispose();
        }
        finally
        {
            await ExitAllOk([job]);
        }
    }

    [SkippableFact]
    public async Task A_mutex_someone_else_holds_with_no_access_for_us_does_not_stop_ca()
    {
        // Another process in the session (elevated, or another account via runas) can create the name first; the mutex
        // is advisory, so ca.exe must still work rather than crash on every click.
        using var squatter = System.Threading.MutexAcl.Create(false, AppMutex.Name, out var created, new System.Security.AccessControl.MutexSecurity());
        Skip.IfNot(created, "A ca.exe is running; the name is already taken.");

        await ExitAllOk([Ca("dev.copy", _dir.File("1.txt"))]);

        Assert.True(File.Exists(_dir.Combine("1 (copy).txt")));
    }

    [SkippableFact]
    public async Task A_menu_command_that_hits_a_registry_error_exits_1_with_the_reason_instead_of_crashing()
    {
        // Setup reads an unexpected exit code as "Windows blocked ca.exe", so an ordinary failure must not crash.
        // It runs a real uninstall-menu, so it would silently remove the menu of an installed ConvertAnything (or a
        // development build's); skip while one is registered.
        Skip.If(MenuIsRegistered(), "A ConvertAnything menu is registered on this machine; uninstall-menu would remove it.");
        const string locked = @"Software\Classes\ConvertAnything.Menu.zz-locked-by-test";
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        var deny = new System.Security.AccessControl.RegistryAccessRule(me.User!, System.Security.AccessControl.RegistryRights.Delete,
            System.Security.AccessControl.AccessControlType.Deny);
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(locked))
        {
            var acl = key.GetAccessControl();
            acl.AddAccessRule(deny);
            key.SetAccessControl(acl);
        }

        try
        {
            var start = new ProcessStartInfo(CaExe, "uninstall-menu") { UseShellExecute = false, RedirectStandardError = true };
            using var ca = Process.Start(start)!;
            var stderr = await ca.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await ca.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, ca.ExitCode);
            Assert.Contains("uninstall-menu", stderr, StringComparison.Ordinal);
        }
        finally
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(locked, writable: true))
            {
                var acl = key!.GetAccessControl();
                acl.RemoveAccessRule(deny);
                key.SetAccessControl(acl);
            }

            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(locked, throwOnMissingSubKey: false);
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
        try
        {
            var stderr = await ca.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await ca.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(ExitCodes.RefusedLocation, ca.ExitCode);
            Assert.Contains("other accounts can modify its folder", stderr, StringComparison.Ordinal);
        }
        finally
        {
            // A regression would leave a real menu pointing into a temp folder that is about to be deleted.
            if (ca.HasExited && ca.ExitCode == 0)
            {
                using var uninstall = Process.Start(new ProcessStartInfo(CaExe, "uninstall-menu") { UseShellExecute = false })!;
                await uninstall.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    [Fact]
    public async Task Check_location_refuses_a_folder_other_accounts_can_change_with_its_own_exit_code()
    {
        // Setup runs this from its private temporary folder before copying anything into the install folder.
        using var dir = new TempDir();
        var acl = new DirectoryInfo(dir.Path).GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
            System.Security.AccessControl.FileSystemRights.Modify, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(dir.Path).SetAccessControl(acl);

        var start = new ProcessStartInfo(CaExe) { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("check-location");
        start.ArgumentList.Add(dir.Path);
        using var ca = Process.Start(start)!;
        var stderr = await ca.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await ca.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ExitCodes.RefusedLocation, ca.ExitCode);
        Assert.Contains("other accounts can modify", stderr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Check_location_accepts_a_folder_only_this_user_can_change()
    {
        // The control for the refusals: under the user's own profile, not %TEMP%, which some machines share with
        // sandbox accounts.
        var dir = Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ca-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // Skipping on the function under test would hide a false refusal, so CI runners, whose profile is a plain
            // Windows one, never skip.
            Skip.If(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
                && !ConvertAnything.Core.Menu.InstallLocation.IsWritableOnlyByOwnerAndAdmins(dir),
                "Other accounts can change %LOCALAPPDATA% or a folder above it on this machine.");
            var start = new ProcessStartInfo(CaExe) { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add("check-location");
            start.ArgumentList.Add(dir);
            using var ca = Process.Start(start)!;
            var stderr = await ca.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await ca.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(ca.ExitCode == ExitCodes.Ok, $"exit {ca.ExitCode}: {stderr}");
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public async Task Check_location_refuses_a_folder_that_does_not_exist()
    {
        using var dir = new TempDir();

        var start = new ProcessStartInfo(CaExe) { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("check-location");
        start.ArgumentList.Add(Path.Combine(dir.Path, "missing"));
        using var ca = Process.Start(start)!;
        await ca.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await ca.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ExitCodes.RefusedLocation, ca.ExitCode);
    }

    private static bool MenuIsRegistered()
    {
        using var classes = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes");
        if (classes is null)
            return false;
        if (classes.GetSubKeyNames().Any(k => k.StartsWith("ConvertAnything", StringComparison.OrdinalIgnoreCase)))
            return true;
        using var associations = classes.OpenSubKey("SystemFileAssociations");
        return associations?.GetSubKeyNames().Any(ext =>
        {
            using var entry = associations.OpenSubKey($@"{ext}\shell\ConvertAnything");
            return entry is not null;
        }) ?? false;
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
        if (_slowCopyMs is not null)
            start.Environment["CONVERTANYTHING_SLOW_COPY_MS"] = _slowCopyMs;
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
