using ConvertAnything.Core;
using ConvertAnything.Core.Menu;
using ConvertAnything.Core.Operations;
using ConvertAnything.Image;

namespace ConvertAnything.Cli;

internal static class Program
{
    private const int Ok = 0;
    private const int SomeFilesFailed = 1;
    private const int BadUsage = 2;
    // The installer tells this apart from other failures: running setup again cannot fix it.
    private const int RefusedLocation = 3;
    // CONVERTANYTHING_BATCH_IDLE_MS is for integration tests on a loaded machine, like CONVERTANYTHING_LOG_DIR.
    private static readonly TimeSpan BatchIdleWindow = TimeSpan.FromMilliseconds(
        int.TryParse(Environment.GetEnvironmentVariable("CONVERTANYTHING_BATCH_IDLE_MS"), out var ms) ? Math.Clamp(ms, 100, 10_000) : 400);
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);

    [STAThread]
    private static int Main(string[] args)
    {
        var invocationDirectory = Environment.CurrentDirectory;
        // Held until the process exits; see AppMutex.
        using var running = AppMutex.TryHold();
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
            Console.Error.WriteLine($"ca {args[0]} failed: {e.Message}");
            return SomeFilesFailed;
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
                        Console.Error.WriteLine($"Refusing to register {exe}: other accounts can modify its folder, or it is reached through a link, so they could run code as you. Install with the ConvertAnything installer instead.");
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
                return SomeFilesFailed;

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
            SecureDirectory.TrySecure(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConvertAnything"));
            log.Prune(LogRetention, DateOnly.FromDateTime(DateTime.Now));
            if (Environment.GetEnvironmentVariable("CONVERTANYTHING_HEADLESS") == "1")
                return RunHeadless(operation, leader, log);
            return BatchUi.Run(operation, leader, log) ? Ok : SomeFilesFailed;
        }
        finally
        {
            leader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Same batch without progress window or notification, for scripts and integration tests.</summary>
    private static int RunHeadless(IOperation operation, BatchLeader leader, RunLog log)
    {
        var outcome = new Runner(log)
            .RunAsync(operation, leader.ReadAllAsync(CancellationToken.None), () => leader.Pending, progress: null, CancellationToken.None)
            .GetAwaiter().GetResult();
        return outcome.NoFailures ? Ok : SomeFilesFailed;
    }
}
