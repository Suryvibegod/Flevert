using System.Diagnostics;

namespace Flevert.Core.Tests;

/// <summary>Runs the real flevert.exe as separate processes, the way Explorer does, with UI disabled.</summary>
[Collection(nameof(SequentialBatchTests))]
public sealed class ProcessBatchIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    // A late process must start within the leader's idle window; a full test run can starve it of CPU for longer than
    // the product's 400 ms (ADR-0002 accepts the resulting split). Tests that need one batch widen the window.
    private string? _idleWindowMs;

    // How long dev.slow-copy takes per file (FLEVERT_SLOW_COPY_MS; 1 s by default).
    private string? _slowCopyMs;

    // How long dev.split takes per page (FLEVERT_DEV_PAGE_MS; 100 ms by default).
    private string? _devPageMs;

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

        var processes = files.Select(f => StartFlevert("dev.copy", f)).ToList();
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
        Skip.If(Environment.ProcessorCount < 3, "flevert.exe runs files in parallel only with three or more cores.");
        _idleWindowMs = "3000";
        var files = Enumerable.Range(1, 6).Select(i => _dir.File($"{i}.txt")).ToList();

        await ExitAllOk(files.Select(f => StartFlevert("dev.slow-copy", f)).ToList());

        // Each log line is written when its file is done, with its duration: rebuild when each file ran.
        var runs = Directory.GetFiles(_logs.Path, "flevert-*.log").SelectMany(File.ReadAllLines).Select(line =>
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
        // file is still converting: only flevert.exe telling the leader that a file is running keeps the batch open (a
        // parallel runner has already asked for the next file).
        _idleWindowMs = "200";
        _slowCopyMs = "6000"; // room for the late process to start slowly; it must arrive while file 1 still runs
        var leader = StartFlevert("dev.slow-copy", _dir.File("1.txt"));
        await Task.Delay(TimeSpan.FromSeconds(2.2));

        var late = StartFlevert("dev.slow-copy", _dir.File("2.txt"));
        await ExitAllOk([leader, late]);

        Assert.Equal(2, Directory.GetFiles(_dir.Path, "* (copy).txt").Length);
        Assert.Equal([leader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)], LoggedProcessIds());
    }

    [SkippableTheory]
    [InlineData(2, true)] // several conversions at once must not make the PC sluggish
    [InlineData(1, false)] // one alone must not be starved past ImageMagick's per-image time limit
    public async Task A_leader_lowers_its_priority_only_while_files_run_in_parallel(int files, bool lowered)
    {
        Skip.If(Environment.ProcessorCount < 3, "flevert.exe runs files in parallel only with three or more cores.");
        // flevert.exe inherits this process's priority (below normal on CI runners) and only ever lowers it.
        using var self = Process.GetCurrentProcess();
        var inherited = self.PriorityClass;
        var below = inherited is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal ? inherited : ProcessPriorityClass.BelowNormal;
        Skip.If(lowered && below == inherited, $"This test runs at {inherited}; flevert.exe cannot go lower than that.");
        _slowCopyMs = "3000";
        var leader = StartFlevert("dev.slow-copy", [.. Enumerable.Range(1, files).Select(i => _dir.File($"{i}.txt"))]);
        try
        {
            // A cold start can be slow on CI: watch for up to 2.5 s of the 3 s the files take.
            var seen = inherited;
            for (var waited = 0; waited < 50 && seen == inherited && !leader.HasExited; waited++)
            {
                await Task.Delay(50);
                leader.Refresh();
                seen = leader.PriorityClass;
            }

            Assert.Equal(lowered ? below : inherited, seen);
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
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.GetDirectoryName(FlevertExe)}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!)
        {
            await mklink.WaitForExitAsync();
            Assert.Equal(0, mklink.ExitCode);
        }

        var viaLink = Path.Combine(link, "flevert.exe");
        _idleWindowMs = "3000";
        var leader = StartFlevertVia(viaLink, "dev.slow-copy", _dir.File("1.txt"), _dir.File("2.txt"));
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var late = StartFlevertVia(viaLink, "dev.slow-copy", _dir.File("3.txt"));

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
    public async Task A_running_flevert_holds_the_app_mutex_the_installer_waits_for()
    {
        // Without it, uninstalling during a batch left flevert.exe and its DLLs behind with no uninstaller to remove them.
        // Another suite may run flevert.exe at the same time; only a mutex that appears with this job proves anything.
        // Dispose what was opened: a handle kept by this process would keep the name alive and skip the next test too.
        var othersRunning = Mutex.TryOpenExisting(AppMutex.Name, out var before);
        before?.Dispose();
        Skip.If(othersRunning, "Another flevert.exe is running.");
        var job = StartFlevert("dev.slow-copy", _dir.File("1.txt"), _dir.File("2.txt"));
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
    public async Task A_mutex_someone_else_holds_with_no_access_for_us_does_not_stop_flevert()
    {
        // Another process in the session (elevated, or another account via runas) can create the name first; the mutex
        // is advisory, so flevert.exe must still work rather than crash on every click.
        using var squatter = System.Threading.MutexAcl.Create(false, AppMutex.Name, out var created, new System.Security.AccessControl.MutexSecurity());
        Skip.IfNot(created, "A flevert.exe is running; the name is already taken.");

        await ExitAllOk([StartFlevert("dev.copy", _dir.File("1.txt"))]);

        Assert.True(File.Exists(_dir.Combine("1 (copy).txt")));
    }

    [SkippableFact]
    public async Task A_menu_command_that_hits_a_registry_error_exits_1_with_the_reason_instead_of_crashing()
    {
        // Setup reads an unexpected exit code as "Windows blocked flevert.exe", so an ordinary failure must not crash.
        // It runs a real uninstall-menu, so it would silently remove the menu of an installed Flevert (or a
        // development build's); skip while one is registered.
        Skip.If(MenuIsRegistered(), "A Flevert menu is registered on this machine; uninstall-menu would remove it.");
        const string locked = @"Software\Classes\Flevert.Menu.zz-locked-by-test";
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
            var start = new ProcessStartInfo(FlevertExe, "uninstall-menu") { UseShellExecute = false, RedirectStandardError = true };
            using var flevert = Process.Start(start)!;
            var stderr = await flevert.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(1, flevert.ExitCode);
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
        await MakeJunction(link, Path.GetDirectoryName(FlevertExe)!);

        var start = new ProcessStartInfo(Path.Combine(link, "flevert.exe"), "install-menu") { UseShellExecute = false, RedirectStandardError = true };
        using var flevert = Process.Start(start)!;
        try
        {
            var stderr = await flevert.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(ExitCodes.RefusedLocation, flevert.ExitCode);
            Assert.Contains("other accounts can modify its folder", stderr, StringComparison.Ordinal);
        }
        finally
        {
            // A regression would leave a real menu pointing into a temp folder that is about to be deleted.
            if (flevert.HasExited && flevert.ExitCode == 0)
            {
                using var uninstall = Process.Start(new ProcessStartInfo(FlevertExe, "uninstall-menu") { UseShellExecute = false })!;
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

        var start = new ProcessStartInfo(FlevertExe) { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("check-location");
        start.ArgumentList.Add(dir.Path);
        using var flevert = Process.Start(start)!;
        var stderr = await flevert.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ExitCodes.RefusedLocation, flevert.ExitCode);
        Assert.Contains("other accounts can modify", stderr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Check_location_accepts_a_folder_only_this_user_can_change()
    {
        // The control for the refusals: under the user's own profile, not %TEMP%, which some machines share with
        // sandbox accounts.
        var dir = Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flevert-test-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // Skipping on the function under test would hide a false refusal, so CI runners, whose profile is a plain
            // Windows one, never skip.
            Skip.If(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
                && !Flevert.Core.Menu.InstallLocation.IsWritableOnlyByOwnerAndAdmins(dir),
                "Other accounts can change %LOCALAPPDATA% or a folder above it on this machine.");
            var start = new ProcessStartInfo(FlevertExe) { UseShellExecute = false, RedirectStandardError = true };
            start.ArgumentList.Add("check-location");
            start.ArgumentList.Add(dir);
            using var flevert = Process.Start(start)!;
            var stderr = await flevert.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(flevert.ExitCode == ExitCodes.Ok, $"exit {flevert.ExitCode}: {stderr}");
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

        var start = new ProcessStartInfo(FlevertExe) { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("check-location");
        start.ArgumentList.Add(Path.Combine(dir.Path, "missing"));
        using var flevert = Process.Start(start)!;
        await flevert.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ExitCodes.RefusedLocation, flevert.ExitCode);
    }

    private static bool MenuIsRegistered()
    {
        using var classes = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes");
        if (classes is null)
            return false;
        if (classes.GetSubKeyNames().Any(k => k.StartsWith("Flevert", StringComparison.OrdinalIgnoreCase)))
            return true;
        using var associations = classes.OpenSubKey("SystemFileAssociations");
        return associations?.GetSubKeyNames().Any(ext =>
        {
            using var entry = associations.OpenSubKey($@"{ext}\shell\Flevert");
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
        // A decoy "budget.txt." beside a protected budget.txt must not make flevert convert budget.txt.
        _dir.File("budget.txt");

        using var flevert = StartFlevert("dev.copy", _dir.Combine("budget.txt."));
        await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(File.Exists(_dir.Combine("budget (copy).txt")));
    }

    [Fact]
    public async Task A_folder_name_ending_in_a_dot_is_not_resolved_to_the_folder_without_it()
    {
        var real = Directory.CreateDirectory(_dir.Combine("sub")).FullName;
        File.WriteAllText(Path.Combine(real, "x.txt"), "protected");
        var decoy = Directory.CreateDirectory(@"\\?\" + _dir.Combine("sub.")).FullName;
        File.WriteAllText(Path.Combine(decoy, "x.txt"), "decoy");

        using var flevert = StartFlevert("dev.copy", _dir.Path + @"\sub.\x.txt");
        await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

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

        using var flevert = StartFlevert("dev.copy", shortPath);
        await ExitAllOk([flevert]);

        Assert.True(File.Exists(_dir.Combine("Ảnh Đà Lạt rất dài tên (copy).txt")), string.Join(", ", Directory.GetFiles(_dir.Path)));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [System.Runtime.InteropServices.Out] char[] shortPath, uint length);

    [Fact]
    public async Task Dev_split_writes_a_whole_folder_of_pages_and_a_second_run_gets_its_own_folder()
    {
        _devPageMs = "0";
        var file = _dir.File("Báo cáo 1.txt");

        await ExitAllOk([StartFlevert("dev.split", file)]);
        await ExitAllOk([StartFlevert("dev.split", file)]);

        Assert.Equal(["Báo cáo 1 (pages)", "Báo cáo 1 (pages) (1)", "Báo cáo 1.txt"],
            Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(50, Directory.GetFiles(_dir.Combine("Báo cáo 1 (pages)")).Length);
        Assert.Equal(50, Directory.GetFiles(_dir.Combine("Báo cáo 1 (pages) (1)")).Length);
        Assert.Equal("Page 50 of 50", (await File.ReadAllTextAsync(_dir.Combine(@"Báo cáo 1 (pages)\Báo cáo 1 50.txt"))).TrimEnd());
    }

    // The tests of joining operations below run at the product's windows: no FLEVERT_BATCH_IDLE_MS.

    [Fact]
    public async Task Twelve_processes_of_a_joining_operation_give_one_file_in_natural_order()
    {
        var files = Enumerable.Range(1, 12).Select(i => _dir.File($"ảnh {i}.txt", $"ảnh {i}\r\n")).ToList();
        int[] startOrder = [7, 12, 1, 10, 3, 2, 11, 5, 9, 4, 8, 6]; // Explorer's order is not the names' order

        var processes = startOrder.Select(i => StartFlevert("dev.join", files[i - 1])).ToList();
        await ExitAllOk(processes);

        var output = _dir.Combine("ảnh 1 (+11).txt");
        // Exactly one output: the inputs plus the joined file, nothing else (no second batch, no .partial file).
        Assert.Equal(
            files.Select(Path.GetFileName).Append("ảnh 1 (+11).txt").Order(StringComparer.Ordinal),
            Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(string.Concat(Enumerable.Range(1, 12).Select(i => $"ảnh {i}\r\n")), await File.ReadAllTextAsync(output));
        Assert.Single(LoggedProcessIds());
    }

    [Fact]
    public async Task A_hundred_processes_of_a_joining_operation_give_one_file()
    {
        // The most Explorer starts for one menu click. Under load, 100 processes used to give 39 to 65 outputs: followers
        // waiting for the one pipe gave up after 10 s and joined their file alone.
        var files = Enumerable.Range(1, 100).Select(i => _dir.File($"IMG_{i:D4}.txt", $"{i}\r\n")).ToList();

        var processes = files.OrderBy(_ => Random.Shared.Next()).Select(f => StartFlevert("dev.join", f)).ToList();
        await ExitAllOk(processes, TimeSpan.FromMinutes(3));

        Assert.Equal([.. files.Select(f => Path.GetFileName(f)).Append("IMG_0001 (+99).txt").Order(StringComparer.Ordinal)], _dir.FileNames());
        Assert.Equal(string.Concat(Enumerable.Range(1, 100).Select(i => $"{i}\r\n")), await File.ReadAllTextAsync(_dir.Combine("IMG_0001 (+99).txt")));
    }

    [Fact]
    public async Task A_process_of_the_selection_that_is_slow_to_start_is_waited_for()
    {
        // Explorer created it with the others, but it runs only after the idle window has long passed (as when 100
        // processes start at once on a busy machine). It used to lead a second batch: a second, partial output.
        var files = Enumerable.Range(1, 4).Select(i => _dir.File($"{i}.txt", $"{i}\r\n")).ToList();
        using var slow = SuspendedFlevert("dev.join", files[0]);
        var others = files.Skip(1).Select(f => StartFlevert("dev.join", f)).ToList();

        await Task.Delay(TimeSpan.FromSeconds(6));
        slow.Resume();
        await ExitAllOk([.. others, slow.Process]);

        Assert.Equal(["1 (+3).txt", "1.txt", "2.txt", "3.txt", "4.txt"], _dir.FileNames());
        Assert.Equal("1\r\n2\r\n3\r\n4\r\n", await File.ReadAllTextAsync(_dir.Combine("1 (+3).txt")));
    }

    [Fact]
    public async Task A_process_created_before_its_batch_closed_that_runs_after_it_is_left_out_with_no_second_output()
    {
        var files = Enumerable.Range(1, 3).Select(i => _dir.File($"{i}.txt", $"{i}\r\n")).ToList();
        var first = files.Take(2).Select(f => StartFlevert("dev.join", f)).ToList();
        // Created after a pause long enough to look like another selection, so the leader does not wait for it.
        await Task.Delay(TimeSpan.FromSeconds(2.6));
        using var straggler = SuspendedFlevert("dev.join", files[2]);
        await ExitAllOk(first);

        straggler.Resume();
        await straggler.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(ExitCodes.Failed, straggler.Process.ExitCode);
        Assert.Equal(["1 (+1).txt", "1.txt", "2.txt", "3.txt"], _dir.FileNames());
        Assert.Contains(LogLines(), line =>
            line.Contains("\t" + files[2] + "\tfailed\t", StringComparison.Ordinal) && line.Contains("\tleft out:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_selection_made_while_another_batch_of_the_operation_collects_is_left_out_not_merged_into_it()
    {
        var a = Enumerable.Range(1, 3).Select(i => _dir.File($"a {i}.txt", $"a{i}\r\n")).ToList();
        var b = Enumerable.Range(1, 2).Select(i => _dir.File($"b {i}.txt", $"b{i}\r\n")).ToList();
        var first = a.Select(f => StartFlevert("dev.join", f)).ToList();
        // The user picks other files and chooses the same command while the first batch still waits for stragglers.
        await Task.Delay(TimeSpan.FromSeconds(2.6));
        var second = b.Select(f => StartFlevert("dev.join", f)).ToList();
        await Task.WhenAll(first.Concat(second).Select(p => p.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(90));

        Assert.All(first, p => Assert.Equal(0, p.ExitCode));
        Assert.Equal([0, 1], second.Select(p => p.ExitCode).Order()); // one notification for the whole second selection
        Assert.Equal("a1\r\na2\r\na3\r\n", await File.ReadAllTextAsync(_dir.Combine("a 1 (+2).txt")));
        Assert.Equal(["a 1 (+2).txt", "a 1.txt", "a 2.txt", "a 3.txt", "b 1.txt", "b 2.txt"], _dir.FileNames());
        Assert.All(b, file => Assert.Contains(LogLines(), line =>
            line.Contains("\t" + file + "\tfailed\t", StringComparison.Ordinal) && line.Contains("\tleft out:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_process_of_the_selection_that_ends_before_handing_over_its_file_fails_the_join_and_is_named()
    {
        // Killed, or Windows could not start it (100 processes can exhaust the desktop heap): joining the rest would give
        // an output that looks complete.
        var files = Enumerable.Range(1, 3).Select(i => _dir.File($"{i}.txt", $"{i}\r\n")).ToList();
        var doomed = SuspendedFlevert("dev.join", files[1]);
        var others = new[] { files[0], files[2] }.Select(f => StartFlevert("dev.join", f)).ToList();
        await Task.Delay(TimeSpan.FromSeconds(4));
        doomed.Process.Kill();
        doomed.Dispose();
        await Task.WhenAll(others.Select(p => p.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal([0, 1], others.Select(p => p.ExitCode).Order());
        Assert.Equal(["1.txt", "2.txt", "3.txt"], _dir.FileNames());
        Assert.Contains(LogLines(), line =>
            line.Contains("\t" + files[1] + "\tfailed\t", StringComparison.Ordinal) && line.Contains("\tnever reached Flevert", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_process_too_slow_to_start_is_named_as_late_and_its_file_brings_no_second_notification()
    {
        // Still suspended when the batch stops waiting for it (JoinGate.HoldLimit): it did not end, it is late. When it
        // runs at last, its batch has already said so: it exits without a "left out" of its own.
        var files = Enumerable.Range(1, 3).Select(i => _dir.File($"{i}.txt", $"{i}\r\n")).ToList();
        using var slow = SuspendedFlevert("dev.join", files[1]);
        var others = new[] { files[0], files[2] }.Select(f => StartFlevert("dev.join", f)).ToList();
        await Task.WhenAll(others.Select(p => p.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(90));

        Assert.Equal([0, 1], others.Select(p => p.ExitCode).Order());
        Assert.Contains(LogLines(), line =>
            line.Contains("\t" + files[1] + "\tfailed\t", StringComparison.Ordinal) && line.Contains("\tdid not reach Flevert in time", StringComparison.Ordinal));

        slow.Resume();
        await slow.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, slow.Process.ExitCode);
        Assert.Equal(["1.txt", "2.txt", "3.txt"], _dir.FileNames());
        Assert.Contains(LogLines(), line =>
            line.Contains("\t" + files[1] + "\tskipped\t", StringComparison.Ordinal) && line.Contains("\tnot joined:", StringComparison.Ordinal));
        Assert.DoesNotContain(LogLines(), line => line.Contains("\tleft out:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_joins_run_one_after_the_other_both_write_their_output()
    {
        // As a script does: the second starts as soon as the first has exited.
        var a = new[] { _dir.File("a 1.txt"), _dir.File("a 2.txt") };
        var b = new[] { _dir.File("b 1.txt"), _dir.File("b 2.txt") };

        await ExitAllOk([.. a.Select(f => StartFlevert("dev.join", f))]);
        await ExitAllOk([.. b.Select(f => StartFlevert("dev.join", f))]);

        Assert.Equal(["a 1 (+1).txt", "a 1.txt", "a 2.txt", "b 1 (+1).txt", "b 1.txt", "b 2.txt"], _dir.FileNames());
    }

    [Fact]
    public async Task Two_installs_running_the_same_operation_at_once_each_form_their_own_single_batch()
    {
        // Another copy of Flevert (a second install, a development build) refuses our processes as peers. When both used
        // the same batch key they kept refusing each other and rotating it, and their batches split. Seen on a machine
        // where several worktrees ran their test suites at the same time.
        using var install = new TempDir();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(FlevertExe)!))
            File.Copy(file, Path.Combine(install.Path, Path.GetFileName(file)));
        var otherExe = Path.Combine(install.Path, "flevert.exe");
        using var otherFiles = new TempDir();
        var ours = Enumerable.Range(1, 8).Select(i => _dir.File($"ảnh {i}.txt", $"{i}\r\n")).ToList();
        var theirs = Enumerable.Range(1, 8).Select(i => otherFiles.File($"ảnh {i}.txt", $"{i}\r\n")).ToList();

        var processes = ours.Zip(theirs).SelectMany(pair => new[]
        {
            StartFlevert("dev.join", pair.First),
            StartFlevertVia(otherExe, "dev.join", pair.Second),
        }).ToList();
        await ExitAllOk(processes);

        // One joined file in each folder, and nothing else: no second batch on either side.
        string[] Expected(IEnumerable<string> inputs) => [.. inputs.Select(f => Path.GetFileName(f)).Append("ảnh 1 (+7).txt").Order(StringComparer.Ordinal)];
        Assert.Equal(Expected(ours), _dir.FileNames());
        Assert.Equal(Expected(theirs), otherFiles.FileNames());
    }

    [Fact]
    public async Task A_joining_batch_with_a_file_it_cannot_use_writes_nothing_and_only_its_leader_exits_with_failure()
    {
        // Exit codes as docs/specs/core.md has them: the leader reports the batch, the others exit 0 once delivered.
        var files = new[] { _dir.File("1.txt"), _dir.File("2.md"), _dir.File("3.txt") };

        var processes = files.Select(f => StartFlevert("dev.join", f)).ToList();
        try
        {
            await Task.WhenAll(processes.Select(p => p.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            foreach (var p in processes.Where(p => !p.HasExited))
                p.Kill(entireProcessTree: true);
        }

        var leader = Assert.Single(LoggedProcessIds());
        Assert.All(processes, p => Assert.Equal(p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == leader ? 1 : 0, p.ExitCode));
        Assert.Equal(["1.txt", "2.md", "3.txt"], _dir.FileNames());
    }

    [Fact]
    public async Task Relative_paths_resolve_against_the_directory_flevert_was_started_in()
    {
        _dir.File("relative.txt");

        await ExitAllOk([StartFlevert("dev.copy", "relative.txt")]);

        Assert.True(File.Exists(_dir.Combine("relative (copy).txt")));
    }

    private Process StartFlevert(string operation, params string[] files) => StartFlevertVia(FlevertExe, operation, files);

    private Process StartFlevertVia(string exe, string operation, params string[] files)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = _dir.Path };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(operation);
        foreach (var f in files)
            start.ArgumentList.Add(f);
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        start.Environment["FLEVERT_HEADLESS"] = "1";
        if (_idleWindowMs is not null)
            start.Environment["FLEVERT_BATCH_IDLE_MS"] = _idleWindowMs;
        if (_slowCopyMs is not null)
            start.Environment["FLEVERT_SLOW_COPY_MS"] = _slowCopyMs;
        if (_devPageMs is not null)
            start.Environment["FLEVERT_DEV_PAGE_MS"] = _devPageMs;
        return Process.Start(start)!;
    }

    /// <summary>
    /// flevert.exe created but not yet running, as Explorer leaves a process that a busy machine is slow to start: its
    /// creation time and command line already exist.
    /// </summary>
    private SuspendedProcess SuspendedFlevert(string operation, string file)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.OrdinalIgnoreCase);
        environment["FLEVERT_LOG_DIR"] = _logs.Path;
        environment["FLEVERT_HEADLESS"] = "1";
        var block = string.Concat(environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).Select(e => $"{e.Key}={e.Value}\0")) + "\0";
        var commandLine = $"\"{FlevertExe}\" run {operation} \"{file}\"";

        const uint CreateSuspended = 0x4, CreateUnicodeEnvironment = 0x400;
        var startup = new StartupInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<StartupInfo>() };
        Assert.True(CreateProcessW(null, commandLine, 0, 0, false, CreateSuspended | CreateUnicodeEnvironment, block, _dir.Path,
            ref startup, out var info), $"CreateProcess failed: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        CloseHandle(info.Process);
        return new SuspendedProcess(Process.GetProcessById(info.ProcessId), info.Thread);
    }

    private sealed class SuspendedProcess(Process process, nint thread) : IDisposable
    {
        private nint _thread = thread;
        private bool _resumed;

        public Process Process { get; } = process;

        public void Resume()
        {
            Assert.NotEqual(uint.MaxValue, ResumeThread(_thread));
            _resumed = true;
        }

        public void Dispose()
        {
            if (_thread != 0)
                CloseHandle(_thread);
            _thread = 0;
            if (!_resumed && !Process.HasExited)
                Process.Kill(); // never resumed: do not leave it behind
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? application, string commandLine, nint processAttributes, nint threadAttributes,
        bool inheritHandles, uint flags, string environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation info);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    private string[] LogLines() => [.. Directory.GetFiles(_logs.Path, "flevert-*.log").SelectMany(File.ReadAllLines)];

    /// <summary>Every process must exit with 0 within 30 s; stragglers are killed so a hang fails instead of stalling.</summary>
    private static Task ExitAllOk(IReadOnlyList<Process> processes) => ExitAllOk(processes, TimeSpan.FromSeconds(30));

    private static async Task ExitAllOk(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(processes.Select(p => p.WaitForExitAsync())).WaitAsync(timeout);
            Assert.All(processes, p => Assert.Equal(0, p.ExitCode));
        }
        finally
        {
            foreach (var p in processes.Where(p => !p.HasExited))
                p.Kill(entireProcessTree: true);
        }
    }

    private string[] LoggedProcessIds() =>
        Directory.GetFiles(_logs.Path, "flevert-*.log").SelectMany(File.ReadAllLines)
            .Select(line => line.Split('\t')[1]).Distinct().ToArray();

    internal static string FlevertExe
    {
        get
        {
            // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = output.Parent!.Name;
            var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            var exe = Path.Combine(root, "src", "Flevert.Cli", "bin", configuration, output.Name, "flevert.exe");
            Assert.True(File.Exists(exe), $"Build the solution first; missing {exe}");
            return exe;
        }
    }
}
