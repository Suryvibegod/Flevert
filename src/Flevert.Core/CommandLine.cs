namespace Flevert.Core;

public abstract record Command;

public sealed record RunCommand(string OperationId, IReadOnlyList<string> Files) : Command;

public sealed record InstallMenuCommand(bool IncludeDev, bool AllowInsecureLocation) : Command;

public sealed record UninstallMenuCommand : Command;

public sealed record ListOpsCommand : Command;

public sealed record ClassicMenuCommand(bool On) : Command;

/// <summary>Setup's check of the install folder before it copies anything there.</summary>
public sealed record CheckLocationCommand(string Directory) : Command;

public sealed record InvalidCommand(string Message) : Command;

public static class CommandLine
{
    public const string Usage = """
        Usage:
          flevert run <op-id> <file>...
          flevert install-menu [--dev] [--allow-insecure-location]
          flevert uninstall-menu
          flevert list-ops
          flevert classic-menu on|off
          flevert check-location <folder>
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
        ["classic-menu", "on"] => new ClassicMenuCommand(On: true),
        ["classic-menu", "off"] => new ClassicMenuCommand(On: false),
        ["check-location", var directory] => new CheckLocationCommand(directory),
        _ => new InvalidCommand(Usage),
    };
}
