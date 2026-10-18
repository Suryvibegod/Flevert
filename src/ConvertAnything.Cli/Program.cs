using ConvertAnything.Core;
using ConvertAnything.Core.Menu;
using ConvertAnything.Core.Operations;

namespace ConvertAnything.Cli;

internal static class Program
{
    private const int Ok = 0;
    private const int SomeFilesFailed = 1;
    private const int BadUsage = 2;
    private static readonly TimeSpan BatchIdleWindow = TimeSpan.FromMilliseconds(400);

    [STAThread]
    private static int Main(string[] args)
    {
        var catalog = new OperationCatalog([new DevCopy(), new DevSlowCopy()]);
        var log = new RunLog(RunLog.DefaultDirectory);

        switch (CommandLine.Parse(args))
        {
            case RunCommand run:
                return RunBatch(run, catalog, log);

            case InstallMenuCommand install:
                var ops = catalog.All.Where(op => install.IncludeDev || !op.Id.StartsWith("dev.", StringComparison.Ordinal));
                new MenuInstaller(new WindowsRegistry(), Environment.ProcessPath!).Install(ops);
                ShellNotify.AssociationsChanged();
                return Ok;

            case UninstallMenuCommand:
                new MenuInstaller(new WindowsRegistry(), Environment.ProcessPath!).Uninstall();
                ShellNotify.AssociationsChanged();
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

        // No synchronization context exists yet, so blocking here cannot deadlock. Followers exit without loading WinForms.
        var inputs = run.Files.Select(InputPath.ToLongPath).ToList();
        var batch = Batch.CollectAsync(run.OperationId, inputs, BatchIdleWindow, CancellationToken.None).GetAwaiter().GetResult();
        if (batch is null)
            return Ok;

        var files = batch.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return BatchUi.Run(operation, files, log) ? Ok : SomeFilesFailed;
    }
}
