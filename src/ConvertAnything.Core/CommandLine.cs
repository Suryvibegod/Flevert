namespace ConvertAnything.Core;

public abstract record Command;

public sealed record RunCommand(string OperationId, IReadOnlyList<string> Files) : Command;

public sealed record InstallMenuCommand(bool IncludeDev, bool AllowInsecureLocation) : Command;

public sealed record UninstallMenuCommand : Command;

public sealed record ListOpsCommand : Command;

public sealed record InvalidCommand(string Message) : Command;

public static class CommandLine
{
    public const string Usage = """
        Usage:
          ca run <op-id> <file>...
          ca install-menu [--dev] [--allow-insecure-location]
          ca uninstall-menu
          ca list-ops
        """;

    public static Command Parse(string[] args) => args switch
    {
        ["run", var opId, .. var files] when files.Length > 0 => new RunCommand(opId, files),
        ["install-menu", .. var flags] when flags.All(f => f is "--dev" or "--allow-insecure-location") =>
            new InstallMenuCommand(
                IncludeDev: flags.Contains("--dev"),
                AllowInsecureLocation: flags.Contains("--allow-insecure-location")),
        ["uninstall-menu"] => new UninstallMenuCommand(),
        ["list-ops"] => new ListOpsCommand(),
        _ => new InvalidCommand(Usage),
    };
}
