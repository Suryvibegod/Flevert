using System.Diagnostics;

namespace ConvertAnything.Core.Tests;

/// <summary>Runs the real ca.exe as separate processes, the way Explorer does, with UI disabled.</summary>
public sealed class ProcessBatchIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Twenty_processes_started_back_to_back_form_one_batch()
    {
        var files = Enumerable.Range(1, 20).Select(i => _dir.File($"ảnh {i}.txt")).ToList();
        var timer = Stopwatch.StartNew();

        await Task.WhenAll(files.Select(f => Ca("dev.copy", f)).ToList().Select(p => p.WaitForExitAsync()));

        // Headless: the leader exits as soon as the batch is done instead of waiting for a notification.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4), $"took {timer.Elapsed}");
        Assert.Equal(20, Directory.GetFiles(_dir.Path, "* (copy).txt").Length);
        Assert.Single(LoggedProcessIds());
    }

    [Fact]
    public async Task Process_started_while_the_leader_is_busy_joins_its_batch()
    {
        var leader = Ca("dev.slow-copy", _dir.File("1.txt"), _dir.File("2.txt"));
        await Task.Delay(TimeSpan.FromSeconds(1.2)); // well past the 400 ms idle window, while file 2 is still running

        var late = Ca("dev.slow-copy", _dir.File("3.txt"));
        await Task.WhenAll(leader.WaitForExitAsync(), late.WaitForExitAsync());

        Assert.Equal(3, Directory.GetFiles(_dir.Path, "* (copy).txt").Length);
        Assert.Equal([leader.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)], LoggedProcessIds());
    }

    private Process Ca(string operation, params string[] files)
    {
        var start = new ProcessStartInfo(CaExe) { UseShellExecute = false };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(operation);
        foreach (var f in files)
            start.ArgumentList.Add(f);
        start.Environment["CONVERTANYTHING_LOG_DIR"] = _logs.Path;
        start.Environment["CONVERTANYTHING_HEADLESS"] = "1";
        return Process.Start(start)!;
    }

    private string[] LoggedProcessIds() =>
        Directory.GetFiles(_logs.Path, "ca-*.log").SelectMany(File.ReadAllLines)
            .Select(line => line.Split('\t')[1]).Distinct().ToArray();

    private static string CaExe
    {
        get
        {
            // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = output.Parent!.Name;
            var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
            var exe = Path.Combine(root, "src", "ConvertAnything.Cli", "bin", configuration, output.Name, "ca.exe");
            Assert.True(File.Exists(exe), $"Build the solution first; missing {exe}");
            return exe;
        }
    }
}
