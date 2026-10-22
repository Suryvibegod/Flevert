using System.Diagnostics;
using System.Text;
using Flevert.Core;

namespace Flevert.Media.Tests;

/// <summary>
/// The ffmpeg the tests run. On Windows it is the product's own: the pinned ffmpeg.exe and ffprobe.exe the build copies
/// into the test output's <c>ffmpeg</c> folder, run through <see cref="ExternalTool"/>. Elsewhere (a developer's Linux or
/// macOS machine, where ExternalTool's job objects do not exist) it is the ffmpeg on the PATH, run by <see cref="RunPortableAsync"/>,
/// so the arguments Flevert builds are still checked against a real ffmpeg; only Windows runs prove the product.
/// </summary>
internal static class TestFfmpeg
{
    public static Ffmpeg Instance { get; } = OperatingSystem.IsWindows()
        ? Ffmpeg.NextToApp
        : new Ffmpeg(OnPath("ffmpeg"), OnPath("ffprobe"), RunPortableAsync);

    /// <summary>Runs ffmpeg with exactly these arguments (fixtures use lavfi, which Flevert itself never allows).</summary>
    public static async Task RunRawAsync(params string[] arguments)
    {
        var run = await Instance.Run(Instance.FfmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-y", .. arguments],
            TimeSpan.FromMinutes(2), null, CancellationToken.None);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg {string.Join(' ', arguments)} failed: {run.StandardError}");
    }

    /// <summary>Runs ffprobe with exactly these arguments and returns what it printed.</summary>
    public static async Task<string> ProbeRawAsync(params string[] arguments)
    {
        var run = await Instance.Run(Instance.FfprobePath, ["-hide_banner", "-v", "error", .. arguments], TimeSpan.FromMinutes(1), null,
            CancellationToken.None);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe {string.Join(' ', arguments)} failed: {run.StandardError}");
        return run.StandardOutput;
    }

    private static string OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(folder => Path.Combine(folder, name))
        .FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException($"The media tests need {name} on the PATH outside Windows.");

    /// <summary>What ExternalTool promises its callers, without job objects: lines as they come, a kill on timeout or cancel.</summary>
    private static async Task<ExternalToolResult> RunPortableAsync(
        string toolPath, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string>? standardOutputLine, CancellationToken ct)
    {
        var start = new ProcessStartInfo(toolPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var callbackFailed = new CancellationTokenSource();
        Exception? callbackError = null;
        var stdout = new StringBuilder();
        var output = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                stdout.Append(line).Append('\n');
                if (callbackError is not null || standardOutputLine is null)
                    continue;
                try
                {
                    standardOutputLine(line);
                }
#pragma warning disable CA1031 // rethrown below, as ExternalTool does
                catch (Exception e)
#pragma warning restore CA1031
                {
                    callbackError = e;
                    await callbackFailed.CancelAsync();
                }
            }
        }, CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timer = new CancellationTokenSource(timeout);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token, callbackFailed.Token);
        try
        {
            await process.WaitForExitAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (callbackError is not null)
                throw callbackError;
            throw new TimeoutException($"{Path.GetFileName(toolPath)} did not finish within {timeout.TotalSeconds:0.#} s.");
        }

        await output;
        if (callbackError is not null)
            throw callbackError;
        return new ExternalToolResult(process.ExitCode, stdout.ToString(), await error, false, false);
    }
}
