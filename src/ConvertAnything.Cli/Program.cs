using ConvertAnything.Core;
using ConvertAnything.Core.Menu;
using ConvertAnything.Core.Operations;
using ConvertAnything.Image;

namespace ConvertAnything.Cli;

internal static class Program
{
    private const int Ok = ExitCodes.Ok;
    private const int SomeFilesFailed = ExitCodes.Failed;
    private const int SetupCommandFailed = ExitCodes.Failed;
    private const int BadUsage = ExitCodes.BadUsage;
    private const int RefusedLocation = ExitCodes.RefusedLocation;
    // CONVERTANYTHING_BATCH_IDLE_MS is for integration tests on a loaded machine, like CONVERTANYTHING_LOG_DIR.
    private static readonly TimeSpan BatchIdleWindow = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("CONVERTANYTHING_BATCH_IDLE_MS"), out var ms) ? Math.Clamp(ms, 100, 10_000) : 400);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);
    // Up to what the operation allows, leaving one core for the machine and the progress window; beyond 8 the gain is
    // small and memory grows. SPEC-core, "Xử lý song song trong một lô".
    private static int MaxParallelFor(IOperation operation) =>
        Math.Clamp(Math.Min(operation.MaxParallel, Environment.ProcessorCount - 1), 1, 8);

    // Never disposed: Inno's AppMutex expects it to exist until the process has fully terminated, which is after Main
    // returns (a using declaration released it there). Windows closes it on exit. See AppMutex.
    private static Mutex? _running;

    [STAThread]
    private static int Main(string[] args)
    {
        var invocationDirectory = Environment.CurrentDirectory;
        _running = AppMutex.TryHold();
        SafeStartup.Apply();
        var catalog = new OperationCatalog([.. ImageOperations.All, new DevCopy(), new DevSlowCopy()]);
        var log = new RunLog(RunLog.DefaultDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Write("(process)", new FileResult(string.Join(' ', args), null, e.ExceptionObject.ToString()), TimeSpan.Zero);

        var command = CommandLine.Parse(args);
        if (command is RunCommand run)
        {
            // SafeStartup moved the current directory, so resolve relative paths against where ca was started.
            // GetFullPath drops trailing dots and spaces from names, which would name another file; keep such paths
            // unchanged for the runner to refuse.
            return RunBatch(run with
            {
                Files = [.. run.Files.Select(f => InputPath.HasNameEndingInDotOrSpace(f) ? f : Path.GetFullPath(f, invocationDirectory))],
            }, catalog, log);
        }

        ParentConsole.Attach();
        try
        {
            return RunSetupCommand(command, catalog);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // The installer treats an unexpected exit code as "Windows blocked ca.exe"; say what actually failed.
            Console.Error.WriteLine($"ca {args.FirstOrDefault()} failed: {e.Message}");
            return SetupCommandFailed;
        }
    }

    private static int RunSetupCommand(Command command, OperationCatalog catalog)
    {
        switch (command)
        {
            case InstallMenuCommand install:
                var exe = Environment.ProcessPath!;
                if (Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Run ca.exe itself; a menu registered through dotnet.exe would not work.");
                    return BadUsage;
                }

                if (!InstallLocation.IsWritableOnlyByOwnerAndAdmins(Path.GetDirectoryName(exe)!))
                {
                    if (!install.AllowInsecureLocation)
                    {
                        Console.Error.WriteLine($"Refusing to register {exe}: other accounts can modify its folder, or it is reached through a link, so they could run code as you (or its permissions could not be read, or it is not on a local NTFS drive). Install with the ConvertAnything installer instead.");
                        return RefusedLocation;
                    }

                    Console.Error.WriteLine($"Warning: other accounts can modify {Path.GetDirectoryName(exe)}, or it is reached through a link. Use --allow-insecure-location only on a machine you trust.");
                }

                var ops = catalog.All.Where(op => install.IncludeDev || !op.Id.StartsWith("dev.", StringComparison.Ordinal));
                new MenuInstaller(new WindowsRegistry(), exe).Install(ops);
                ShellNotify.AssociationsChanged();
                return Ok;

            case UninstallMenuCommand:
                new MenuInstaller(new WindowsRegistry(), Environment.ProcessPath!).Uninstall();
                ShellNotify.AssociationsChanged();
                return Ok;

            case ClassicMenuCommand classic:
                var classicMenu = new ClassicMenu(new WindowsRegistry());
                if (!classic.On)
                {
                    classicMenu.Off();
                    return Ok;
                }

                if (classicMenu.On())
                    return Ok;
                Console.Error.WriteLine("The classic-menu key already exists in another form; it was left unchanged, so the menu does not switch.");
                return SetupCommandFailed;

            case CheckLocationCommand check:
                // Run by setup from its own temporary folder, before it copies anything into the install folder.
                if (!Path.IsPathFullyQualified(check.Directory))
                {
                    Console.Error.WriteLine($"Give a full path: {check.Directory}");
                    return BadUsage;
                }

                if (!Directory.Exists(check.Directory))
                {
                    Console.Error.WriteLine($"{check.Directory} does not exist.");
                    return RefusedLocation;
                }

                if (InstallLocation.IsWritableOnlyByOwnerAndAdmins(check.Directory))
                    return Ok;
                Console.Error.WriteLine($"Refusing to install into {check.Directory}: other accounts can modify it or a folder above it, or it is reached through a link, so they could run code as you (or its permissions could not be read, or it is not on a local NTFS drive).");
                return RefusedLocation;

            case ListOpsCommand:
                foreach (var op in catalog.All)
                    Console.WriteLine($"{op.Id}\t{op.MenuPath}\t{string.Join(' ', op.Extensions)}");
                return Ok;

            case InvalidCommand invalid:
                Console.Error.WriteLine(invalid.Message);
                return BadUsage;

            default:
                return BadUsage;
        }
    }

    private static int RunBatch(RunCommand run, OperationCatalog catalog, RunLog log)
    {
        var operation = catalog.Find(run.OperationId);
        if (operation is null)
        {
            log.Write(run.OperationId, new FileResult(string.Join(';', run.Files), null, "unknown operation"), TimeSpan.Zero);
            return BadUsage;
        }

        // No separate 8.3 expansion here: Path.GetFullPath above already expands short names (it calls GetLongPathNameW,
        // which touches the path before any check; accepted, see ADR-0001), and outputs are named from the
        // pinned file's real, long path in any case.
        var inputs = run.Files.ToList();
        // No synchronization context exists yet, so blocking here cannot deadlock. Followers exit without loading WinForms.
        var leader = Batch.LeadOrJoinAsync(run.OperationId, inputs, BatchIdleWindow, CancellationToken.None).GetAwaiter().GetResult();
        if (leader is null)
            return Ok;

        try
        {
            // Logs hold every path processed; keep the whole app-data folder private to this user, like the batch keys.
            SecureDirectory.TrySecure(AppData.Root);
            log.Prune(LogRetention, DateOnly.FromDateTime(DateTime.Now));
            var maxParallel = MaxParallelFor(operation);
            Action<int> running = count =>
            {
                leader.ReportRunning(count);
                FollowRunningCount(count);
            };
            if (Environment.GetEnvironmentVariable("CONVERTANYTHING_HEADLESS") == "1")
                return RunHeadless(operation, leader, log, maxParallel, running);
            return BatchUi.Run(operation, leader, log, maxParallel, running) ? Ok : SomeFilesFailed;
        }
        finally
        {
            leader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static bool _lowered;
    private static System.Diagnostics.ProcessPriorityClass? _startPriority;

    // Below normal while two or more files convert at once, so they do not compete with the user's foreground work.
    // Back to the priority ca.exe started with otherwise (normal from Explorer): ImageMagick's time limit runs per image
    // on the clock, and a large image converting alone (or Explorer opening the log, which inherits it) must not be
    // starved. Never raised: a ca.exe started at idle stays there. Called in order by the runner. Best effort: a batch
    // still runs if Windows refuses.
    private static void FollowRunningCount(int count)
    {
        var lower = count > 1;
        if (lower == _lowered)
            return;
        _lowered = lower;
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            _startPriority ??= self.PriorityClass;
            var below = _startPriority is System.Diagnostics.ProcessPriorityClass.Idle or System.Diagnostics.ProcessPriorityClass.BelowNormal
                ? _startPriority.Value
                : System.Diagnostics.ProcessPriorityClass.BelowNormal;
            self.PriorityClass = lower ? below : _startPriority.Value;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>Same batch without progress window or notification, for scripts and integration tests.</summary>
    private static int RunHeadless(IOperation operation, BatchLeader leader, RunLog log, int maxParallel, Action<int> running)
    {
        var outcome = new Runner(log)
            .RunAsync(operation, leader.ReadAllAsync(CancellationToken.None), () => leader.Pending, progress: null, CancellationToken.None,
                maxParallel, running)
            .GetAwaiter().GetResult();
        return outcome.NoFailures ? Ok : SomeFilesFailed;
    }
}
