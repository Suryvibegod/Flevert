using Flevert.Core;
using Flevert.Core.Menu;
using Flevert.Core.Operations;
using Flevert.Core.Updates;
using Flevert.Image;
using Flevert.Media;
using Flevert.Pdf;

namespace Flevert.Cli;

internal static class Program
{
    private const int Ok = ExitCodes.Ok;
    private const int SomeFilesFailed = ExitCodes.Failed;
    private const int SetupCommandFailed = ExitCodes.Failed;
    private const int BadUsage = ExitCodes.BadUsage;
    private const int RefusedLocation = ExitCodes.RefusedLocation;
    // FLEVERT_BATCH_IDLE_MS is for integration tests on a loaded machine, like FLEVERT_LOG_DIR.
    private static readonly TimeSpan BatchIdleWindow = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("FLEVERT_BATCH_IDLE_MS"), out var ms) ? Math.Clamp(ms, 100, 10_000) : 400);
    // At least this for an operation that joins the batch into one output (still widened by FLEVERT_BATCH_IDLE_MS). Its
    // leader also waits for every process of the selection it can see; this covers the gaps between Explorer starting
    // them (0.5 s at most, measured under load). ADR-0007, part 2.
    private static readonly TimeSpan JoinIdleWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);
    // Up to what the operation allows, leaving one core for the machine and the progress window; beyond 8 the gain is
    // small and memory grows. docs/specs/core.md, "Xử lý song song trong một lô".
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
        var command = CommandLine.Parse(args);
        // Started by flevert.exe itself to read one PDF (ADR-0008); reports on standard output, logs nothing of its own.
        if (command is PdfWorkerCommand)
            return PdfRenderWorker.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());

        var catalog = new OperationCatalog([.. MenuOperations.All, .. MediaOperations.All, new DevCopy(), new DevSlowCopy(), new DevSplit(), new DevJoin()]);
        var log = new RunLog(RunLog.DefaultDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Write("(process)", new FileResult(string.Join(' ', args), null, e.ExceptionObject.ToString()), TimeSpan.Zero);

        if (command is RunCommand run)
        {
            // SafeStartup moved the current directory, so resolve relative paths against where flevert.exe was started.
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
            // The installer treats an unexpected exit code as "Windows blocked flevert.exe"; say what actually failed.
            Console.Error.WriteLine($"flevert {args.FirstOrDefault()} failed: {e.Message}");
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
                    Console.Error.WriteLine("Run flevert.exe itself; a menu registered through dotnet.exe would not work.");
                    return BadUsage;
                }

                if (!InstallLocation.IsWritableOnlyByOwnerAndAdmins(Path.GetDirectoryName(exe)!))
                {
                    if (!install.AllowInsecureLocation)
                    {
                        Console.Error.WriteLine($"Refusing to register {exe}: other accounts can modify its folder, or it is reached through a link, so they could run code as you (or its permissions could not be read, or it is not on a local NTFS drive). Install with the Flevert installer instead.");
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

            case UpdateCheckCommand updateCheck:
                // Run by setup with its check box; a failure is reported but setup carries on.
                UpdateChecker.SetEnabled(new UpdateStateStore(UpdateStateStore.DefaultPath), updateCheck.On);
                return Ok;

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
        // A late file splits a joined batch into two outputs, not just two notifications: wait a little longer. ADR-0007.
        var idle = operation is IBatchOperation && BatchIdleWindow < JoinIdleWindow ? JoinIdleWindow : BatchIdleWindow;
        // No synchronization context exists yet, so blocking here cannot deadlock. Followers exit without loading WinForms.
        var leader = (operation is IBatchOperation
            ? Batch.LeadOrJoinOneOutputAsync(run.OperationId, inputs, idle, CancellationToken.None)
            : Batch.LeadOrJoinAsync(run.OperationId, inputs, idle, CancellationToken.None)).GetAwaiter().GetResult();
        if (leader is null)
            return Ok;

        var headless = Environment.GetEnvironmentVariable("FLEVERT_HEADLESS") == "1";
        // Protect and unlock ask for the password in a dialog, once for the batch when the user agrees. Without windows
        // they keep the catalog's prompt, which fails each file at once rather than wait for an answer (docs/specs/pdf.md).
        DialogPasswordPrompt? passwords = null;
        IPasswordOperation? asking = null;
        if (!headless && operation is IPasswordOperation needsPassword)
        {
            passwords = new DialogPasswordPrompt();
            operation = asking = needsPassword.WithPrompt(passwords);
        }

        try
        {
            // Logs hold every path processed; keep the whole app-data folder private to this user, like the batch keys.
            SecureDirectory.TrySecure(AppData.Root);
            log.Prune(LogRetention, DateOnly.FromDateTime(DateTime.Now));
            var runner = new Runner(log);
            Func<IProgress<BatchProgress>?, CancellationToken, Task<BatchOutcome>> batch = operation switch
            {
                // Files that came too late for their batch: one notification for all of them, and no second output.
                IBatchOperation join when leader.LeavesOut => (_, ct) => runner.LeaveOutAsync(join, leader.ReadAllAsync(ct), ct),
                // Collects the whole batch first, so the leader closes once no file has arrived for the idle window.
                IBatchOperation join => (progress, ct) => runner.RunAsync(join, leader.ReadAllAsync(ct), progress, ct, () => leader.Missing),
                IOperation each => (progress, ct) => runner.RunAsync(
                    each, leader.ReadAllAsync(ct), () => leader.Pending, progress, ct, MaxParallelFor(each), count =>
                    {
                        leader.ReportRunning(count);
                        FollowRunningCount(count);
                    }),
                _ => throw new InvalidOperationException($"{operation.Id} is neither kind of operation."), // the catalog refuses it
            };
            if (headless)
                return batch(null, CancellationToken.None).GetAwaiter().GetResult().NoFailures ? Ok : SomeFilesFailed;
            // Alongside the batch, so it costs no time; asked about after the batch, if a newer version is out.
            return BatchUi.Run(operation, leader.Pending, log, batch, UpdateOffer.Start(), passwords) ? Ok : SomeFilesFailed;
        }
        finally
        {
            asking?.Dispose(); // wipes the passwords it still holds
            leader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static bool _lowered;
    private static System.Diagnostics.ProcessPriorityClass? _startPriority;

    // Below normal while two or more files convert at once, so they do not compete with the user's foreground work.
    // Back to the priority flevert.exe started with otherwise (normal from Explorer): ImageMagick's time limit runs per image
    // on the clock, and a large image converting alone (or Explorer opening the log, which inherits it) must not be
    // starved. Never raised: a flevert.exe started at idle stays there. Called in order by the runner. Best effort: a batch
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
}
