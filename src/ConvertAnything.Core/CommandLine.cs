namespace ConvertAnything.Core;

public abstract record Command;

public sealed record RunCommand(string OperationId, IReadOnlyList<string> Files) : Command;

public sealed record InstallMenuCommand(bool IncludeDev) : Command;

public sealed record UninstallMenuCommand : Command;

public sealed record ListOpsCommand : Command;

public sealed record InvalidCommand(string Message) : Command;

public static class CommandLine
{
    public const string Usage = """
        Usage:
          ca run <op-id> <file>...
          ca install-menu [--dev]
          ca uninstall-menu
          ca list-ops
        """;

    public static Command Parse(string[] args) => args switch
    {
        ["run", var opId, .. var files] when files.Length > 0 => new RunCommand(opId, files),
        ["install-menu"] => new InstallMenuCommand(IncludeDev: false),
        ["install-menu", "--dev"] => new InstallMenuCommand(IncludeDev: true),
        ["uninstall-menu"] => new UninstallMenuCommand(),
        ["list-ops"] => new ListOpsCommand(),
        _ => new InvalidCommand(Usage),
    };
}
