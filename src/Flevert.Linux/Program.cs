using System.Runtime.Versioning;
using Flevert.Core;
using Flevert.Core.Menu;
using Flevert.Core.Operations;
using Flevert.Pdf;

[assembly: SupportedOSPlatform("linux")]

namespace Flevert.Linux;

internal static class Program
{
    /// <summary>As many files as a batch takes on Windows (Batch.MaxFilesPerBatch); Nautilus can select far more.</summary>
    internal const int MaxFiles = 1000;

    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);

    // Up to what the operation allows, leaving one core for the machine and the desktop; beyond 8 the gain is small and
    // memory grows. docs/specs/core.md, "Xử lý song song trong một lô". Unlike on Windows the priority is not lowered
    // while files run together: Linux lets a user lower it but not raise it back, and a large image converting alone
    // must not be starved (ImageMagick's time limit runs on the clock).
    private static int MaxParallelFor(IOperation operation) =>
        Math.Clamp(Math.Min(operation.MaxParallel, Environment.ProcessorCount - 1), 1, 8);

    private static int Main(string[] args)
    {
        var command = CommandLine.Parse(args);
        // Started by flevert itself to read one PDF (ADR-0008); reports on standard output, logs nothing of its own.
        if (command is PdfWorkerCommand)
            return PdfRenderWorker.Run(Console.OpenStandardInput(), Console.OpenStandardOutput());

        // Text from pictures and Make searchable need tesseract, which Flevert has for Windows only so far.
        var catalog = new OperationCatalog([
            .. MenuOperations.All.Where(op => !MenuOperations.NeedTesseract.Contains(op.Id)),
            new DevCopy(), new DevSlowCopy(), new DevSplit(), new DevJoin()]);
        var log = new RunLog(RunLog.DefaultDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Write("(process)", new FileResult(string.Join(' ', args), null, e.ExceptionObject.ToString()), TimeSpan.Zero);

        switch (command)
        {
            case RunCommand run:
                // Nautilus gives absolute paths; a relative one from a terminal is taken from where flevert was started,
                // joined as it is: normalising "a/../b" by its text would name another file when "a" is a link.
                return RunBatch(run with { Files = [.. run.Files.Select(f => Path.Combine(Environment.CurrentDirectory, f))] }, catalog, log);

            case InstallMenuCommand install:
                return InstallMenu(install, catalog);

            case UninstallMenuCommand:
                UnixMenuInstaller.ForThisUser().Uninstall();
                Console.WriteLine("Flevert is out of the Nautilus menu once Nautilus restarts: nautilus -q");
                return ExitCodes.Ok;

            case ListOpsCommand:
                foreach (var op in catalog.All)
                    Console.WriteLine($"{op.Id}\t{op.MenuPath}\t{string.Join(' ', op.Extensions)}");
                return ExitCodes.Ok;

            case InvalidCommand invalid:
                Console.Error.WriteLine(invalid.Message);
                return ExitCodes.BadUsage;

            default:
                Console.Error.WriteLine("This command is not available on Linux.");
                return ExitCodes.BadUsage;
        }
    }

    // Where nautilus-python puts its loader, by distribution; one of them means the extension will be loaded.
    private static readonly string[] NautilusPython =
    [
        "/usr/lib/x86_64-linux-gnu/nautilus/extensions-4/libnautilus-python.so",
        "/usr/lib/x86_64-linux-gnu/nautilus/extensions-3.0/libnautilus-python.so",
        "/usr/lib64/nautilus/extensions-4/libnautilus-python.so",
        "/usr/lib64/nautilus/extensions-3.0/libnautilus-python.so",
        "/usr/lib/nautilus/extensions-4/libnautilus-python.so",
        "/usr/lib/nautilus/extensions-3.0/libnautilus-python.so",
    ];

    private static int InstallMenu(InstallMenuCommand install, OperationCatalog catalog)
    {
        var exe = Environment.ProcessPath!;
        if (Path.GetFileName(exe) == "dotnet")
        {
            Console.Error.WriteLine("Run flevert itself; a menu that starts it through dotnet would not work.");
            return ExitCodes.BadUsage;
        }

        if (!UnixInstallLocation.IsWritableOnlyByOwner(Path.GetDirectoryName(exe)!))
        {
            if (!install.AllowInsecureLocation)
            {
                Console.Error.WriteLine($"Refusing to put {exe} in the menu: other accounts can change its folder or a folder above it, or it is reached through a link, so they could run code as you. Install with install.sh instead.");
                return ExitCodes.RefusedLocation;
            }

            Console.Error.WriteLine($"Warning: other accounts can change {Path.GetDirectoryName(exe)}, or it is reached through a link. Use --allow-insecure-location only on a machine you trust.");
        }

        var ops = catalog.All.Where(op => install.IncludeDev || !op.Id.StartsWith("dev.", StringComparison.Ordinal));
        UnixMenuInstaller.ForThisUser().Install(
            NautilusMenu.Build(ops, exe, MaxFiles), Path.Combine(AppContext.BaseDirectory, "nautilus", UnixMenuInstaller.ExtensionFileName));
        if (!NautilusPython.Any(File.Exists))
        {
            Console.Error.WriteLine("Nautilus loads the Flevert menu through nautilus-python, which is not installed. Install it, then restart Nautilus:");
            Console.Error.WriteLine("  Ubuntu, Debian, Pop!_OS: sudo apt install python3-nautilus");
            Console.Error.WriteLine("  Fedora: sudo dnf install nautilus-python");
            Console.Error.WriteLine("  Arch: sudo pacman -S python-nautilus");
            // The menu is written, so installing nautilus-python is all that is left; but Flevert is not in the menu yet.
            return ExitCodes.Failed;
        }

        Console.WriteLine("Flevert is in the Nautilus menu once Nautilus restarts: nautilus -q");
        return ExitCodes.Ok;
    }

    private static int RunBatch(RunCommand run, OperationCatalog catalog, RunLog log)
    {
        // Logs hold every path processed; keep the whole app-data folder private to this user, before the first line.
        SecureDirectory.TrySecure(AppData.Root);
        var operation = catalog.Find(run.OperationId);
        if (operation is null)
        {
            log.Write(run.OperationId, new FileResult(string.Join(';', run.Files), null, "unknown operation"), TimeSpan.Zero);
            return ExitCodes.BadUsage;
        }

        var headless = Environment.GetEnvironmentVariable("FLEVERT_HEADLESS") == "1";
        if (run.Files.Count > MaxFiles)
        {
            // The menu hands over one file too many, not every file selected (flevert_menu.py): no count to give.
            var refused = $"Flevert takes at most {MaxFiles} files at once. Select fewer and try again.";
            log.Write(run.OperationId, new FileResult($"({run.Files.Count} files)", null, refused), TimeSpan.Zero);
            Console.Error.WriteLine(refused);
            if (!headless)
                DesktopNotification.ShowAsync(new BatchSummary(refused, HasFailures: true, ShowsLog: false)).GetAwaiter().GetResult();
            return ExitCodes.BadUsage;
        }

        // Protect and unlock ask for the password in a dialog, once for the batch when the user agrees; Add watermark…
        // asks for its text the same way. Without windows they keep the catalog's prompts, which fail each file at once
        // rather than wait for an answer (docs/specs/pdf.md).
        IUnixQuestion? question = null;
        IPasswordOperation? asking = null;
        if (!headless && operation is IPasswordOperation needsPassword)
        {
            var passwords = new ZenityPasswordPrompt();
            question = passwords;
            operation = asking = needsPassword.WithPrompt(passwords);
        }

        if (!headless && operation is PdfWatermarkMenu needsText)
        {
            var watermarks = new ZenityWatermarkPrompt();
            question = watermarks;
            operation = needsText.WithPrompt(watermarks);
        }

        // A join sorts its files; setting the C library's collation is not thread-safe, so it is done before any work starts.
        if (operation is IBatchOperation)
            NaturalOrder.Prepare();

        // Nautilus hands every selected file to one flevert: the whole batch is known from the start.
        var leader = BatchLeader.Alone(run.Files);
        try
        {
            log.Prune(LogRetention, DateOnly.FromDateTime(DateTime.Now));
            var runner = new Runner(log);
            Func<IProgress<BatchProgress>?, CancellationToken, Task<BatchOutcome>> batch = operation switch
            {
                IBatchOperation join => (progress, ct) => runner.RunAsync(join, leader.ReadAllAsync(ct), progress, ct, () => leader.Missing),
                IOperation each => (progress, ct) => runner.RunAsync(
                    each, leader.ReadAllAsync(ct), () => leader.Pending, progress, ct, MaxParallelFor(each), leader.ReportRunning),
                _ => throw new InvalidOperationException($"{operation.Id} is neither kind of operation."), // the catalog refuses it
            };
            if (headless)
                return batch(null, CancellationToken.None).GetAwaiter().GetResult().NoFailures ? ExitCodes.Ok : ExitCodes.Failed;
            // An operation outside the menu (a tool run by its id) is named by its id.
            var title = operation.MenuPath.Length > 0 ? operation.MenuPath.Replace('/', ' ') : operation.Id;
            return UnixBatchUi.Run(title, log, batch, question: question) ? ExitCodes.Ok : ExitCodes.Failed;
        }
        finally
        {
            asking?.Dispose(); // wipes the passwords it still holds
            leader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
