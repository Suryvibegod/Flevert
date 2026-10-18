using ConvertAnything.Core;
using ConvertAnything.Core.Menu;
using ConvertAnything.Core.Operations;

namespace ConvertAnything.Cli;

internal static class Program
{
    private const int Ok = 0;
    private const int SomeFilesFailed = 1;
    private const int BadUsage = 2;

    private static async Task<int> Main(string[] args)
    {
        var catalog = new OperationCatalog([new DevCopy()]);
        var log = new RunLog(RunLog.DefaultDirectory);

        switch (CommandLine.Parse(args))
        {
            case RunCommand run:
                var operation = catalog.Find(run.OperationId);
                if (operation is null)
                {
                    log.Write(run.OperationId, new FileResult(string.Join(';', run.Files), null, "unknown operation"), TimeSpan.Zero);
                    return BadUsage;
                }

                var results = await new Runner(log).RunAsync(operation, run.Files, progress: null, CancellationToken.None);
                return results.All(r => r.Succeeded) ? Ok : SomeFilesFailed;

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
                Console.Error.WriteLine("Not implemented yet.");
                return BadUsage;
        }
    }
}
