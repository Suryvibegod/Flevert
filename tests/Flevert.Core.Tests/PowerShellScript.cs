using System.Diagnostics;
using System.Text;

namespace Flevert.Core.Tests;

/// <summary>
/// Runs a script from scripts/ under Windows PowerShell 5.1, as setup-dev.ps1 does on a new machine, or under PowerShell 7
/// (<see cref="Pwsh"/>), which CI uses for fetch-deps.ps1.
/// </summary>
internal static class PowerShellScript
{
    private static readonly string[] Common = ["-NoProfile", "-ExecutionPolicy", "Bypass"];

    /// <summary>pwsh.exe on PATH, or null: CI runners have it, a fresh Windows does not.</summary>
    public static string? Pwsh { get; } = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => Path.Combine(folder.Trim('"'), "pwsh.exe"))
        .FirstOrDefault(File.Exists);

    public static (int ExitCode, string Output) Run(string name, IEnumerable<string> arguments, IDictionary<string, string>? environment = null, string shell = "powershell.exe") =>
        Start(shell, ["-File", Path.Combine(InstallerScriptTests.RepositoryRoot(), "scripts", name), .. arguments], environment, name);

    /// <summary>Runs a command line of PowerShell, for checks the .NET libraries cannot make (Authenticode).</summary>
    public static (int ExitCode, string Output) Command(string command) => Start("powershell.exe", ["-Command", command], null, "the command");

    private static (int ExitCode, string Output) Start(string shell, IEnumerable<string> arguments, IDictionary<string, string>? environment, string name)
    {
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in Common.Concat(arguments))
            start.ArgumentList.Add(argument);
        // Under PowerShell 7 (the CI runner's shell) PSModulePath lists its own modules, which Windows PowerShell cannot
        // load: Get-FileHash and the like would be "not found". Let each shell use its own default.
        start.Environment.Remove("PSModulePath");
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            start.Environment[key] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{name} did not finish in 2 minutes");
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}
