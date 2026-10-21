using Flevert.Core;
using Flevert.Core.Operations;
using Flevert.Pdf;

namespace Flevert.Linux;

internal static class Program
{
    private static int Main(string[] args)
    {
        var catalog = new OperationCatalog([.. MenuOperations.All, new DevCopy(), new DevSlowCopy(), new DevSplit(), new DevJoin()]);
        switch (CommandLine.Parse(args))
        {
            case ListOpsCommand:
                foreach (var op in catalog.All)
                    Console.WriteLine($"{op.Id}\t{op.MenuPath}\t{string.Join(' ', op.Extensions)}");
                return ExitCodes.Ok;

            case InvalidCommand invalid:
                Console.Error.WriteLine(invalid.Message);
                return ExitCodes.BadUsage;

            default:
                Console.Error.WriteLine("Not available on Linux yet.");
                return ExitCodes.BadUsage;
        }
    }
}
