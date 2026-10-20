using System.ComponentModel;
using System.Diagnostics;

namespace Flevert.Media.Tests;

/// <summary>
/// Runs the real flevert.exe: covers the catalog wiring, finding ffmpeg in the <c>ffmpeg</c> folder beside flevert.exe, its
/// priority, and ffmpeg ending with flevert.exe. One class, so its tests never run at the same time (they look for that
/// ffmpeg.exe's processes, which no other test runs: the others use the copy in the test output).
/// </summary>
public sealed class FlevertExeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    private static string FfmpegBesideFlevert => Path.Combine(Path.GetDirectoryName(FlevertExe)!, Ffmpeg.FolderName, "ffmpeg.exe");

    [WindowsFact]
    public async Task Flevert_exe_converts_a_wav_to_mp3_with_the_ffmpeg_beside_it()
    {
        Assert.True(File.Exists(FfmpegBesideFlevert), "ffmpeg.exe is not in the ffmpeg folder beside flevert.exe");
        var input = await Fixtures.CopyAsync(Fixtures.WavAsync(), _dir, "Bài hát.wav");

        using var flevert = StartRun("audio.to-mp3", input);
        try
        {
            await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            if (!flevert.HasExited)
                flevert.Kill(entireProcessTree: true);
        }

        Assert.True(flevert.ExitCode == 0, string.Join('\n', Directory.GetFiles(_logs.Path).Select(File.ReadAllText)));
        var info = await TestFfmpeg.Instance.ProbeAsync(_dir.Combine("Bài hát.mp3"), CancellationToken.None);
        Assert.Equal("mp3", info.MainAudio!.Codec);
    }

    [WindowsFact]
    public async Task Ffmpeg_runs_below_normal_priority_and_ends_when_flevert_exe_is_killed()
    {
        // Two hours of 8 kHz mono: far longer to encode than the waits below, so ffmpeg ending in time means it was killed.
        var input = _dir.Combine("long.wav");
        await TestFfmpeg.RunRawAsync("-f", "lavfi", "-i", "sine=frequency=300:sample_rate=8000:duration=7200", "-c:a", "pcm_u8", "-f", "wav", input);

        using var flevert = StartRun("audio.to-mp3", input);
        Process? ffmpeg = null;
        try
        {
            var waited = Stopwatch.StartNew();
            while ((ffmpeg = FindRunning(FfmpegBesideFlevert)) is null && !flevert.HasExited && waited.Elapsed < TimeSpan.FromSeconds(30))
                await Task.Delay(20);
            Assert.True(ffmpeg is not null, $"ffmpeg.exe never started (flevert.exe exited: {flevert.HasExited})");
            await Task.Delay(200); // lowered right after it starts
            Assert.Equal(ProcessPriorityClass.BelowNormal, ffmpeg.PriorityClass);

            // Not the tree: only what Windows does when flevert.exe dies (closing its handles) may end ffmpeg.
            flevert.Kill(entireProcessTree: false);
            await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            await Task.WhenAny(ffmpeg.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.True(ffmpeg.HasExited, "ffmpeg.exe kept running after flevert.exe was killed");
        }
        finally
        {
            if (!flevert.HasExited)
                flevert.Kill(entireProcessTree: true);
            if (ffmpeg is { HasExited: false })
                ffmpeg.Kill();
            ffmpeg?.Dispose();
        }
    }

    private Process StartRun(string operationId, string input)
    {
        var start = new ProcessStartInfo(FlevertExe) { UseShellExecute = false };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(operationId);
        start.ArgumentList.Add(input);
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        start.Environment["FLEVERT_HEADLESS"] = "1";
        start.Environment["FLEVERT_BATCH_IDLE_MS"] = "100";
        return Process.Start(start)!;
    }

    private static Process? FindRunning(string exePath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                // Exited meanwhile, or not ours to inspect.
            }

            process.Dispose();
        }

        return null;
    }

    private static string FlevertExe
    {
        get
        {
            // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = output.Parent!.Name;
            var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            var exe = Path.Combine(root, "src", "Flevert.Cli", "bin", configuration, output.Name, "flevert.exe");
            Assert.True(File.Exists(exe), $"Build the solution first; missing {exe}");
            return exe;
        }
    }
}
