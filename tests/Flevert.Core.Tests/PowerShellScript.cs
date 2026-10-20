using System.Diagnostics;
using System.Text;

namespace Flevert.Core.Tests;

/// <summary>Runs a script from scripts/ under Windows PowerShell 5.1, as setup-dev.ps1 does on a new machine.</summary>
internal static class PowerShellScript
{
    public static (int ExitCode, string Output) Run(string name, IEnumerable<string> arguments, IDictionary<string, string>? environment = null)
    {
        var script = Path.Combine(InstallerScriptTests.RepositoryRoot(), "scripts", name);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
            start.ArgumentList.Add(argument);
        // Under PowerShell 7 (the CI runner's shell) PSModulePath lists its own modules, which Windows PowerShell cannot
        // load: Get-FileHash and the like would be "not found". Let powershell.exe use its own default.
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
