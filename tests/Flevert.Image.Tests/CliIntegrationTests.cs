using System.Diagnostics;
using ImageMagick;

namespace Flevert.Image.Tests;

/// <summary>Runs the real flevert.exe, so the catalog wiring and Magick's native library loading under SafeStartup are covered.</summary>
public sealed class CliIntegrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task Flevert_converts_a_jpg_to_png()
    {
        var input = _dir.Combine("ảnh.jpg");
        using (var image = new MagickImage(MagickColors.Orange, 12, 8))
            image.Write(input, MagickFormat.Jpeg);

        using var flevert = StartHeadless("image.to-png", input);
        try
        {
            await flevert.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!flevert.HasExited)
                flevert.Kill(entireProcessTree: true);
        }

        Assert.True(flevert.ExitCode == 0, string.Join('\n', Directory.GetFiles(_logs.Path).Select(File.ReadAllText)));
        using var png = new MagickImage(_dir.Combine("ảnh.png"));
        Assert.Equal(MagickFormat.Png, png.Format);
    }

    // T17b: different operations started together are separate batch leaders, each initialising ImageMagick. Before the
    // fix, about half of these rounds had one process fail with "Access to the path is denied" while replacing
    // policy.xml, which another process was reading. A stale policy each round makes every process try to replace it, as
    // on the first run after an update.
    [Fact]
    public async Task Different_image_operations_started_together_all_succeed()
    {
        string[] operations = ["image.to-png", "image.compress-light", "image.to-webp", "image.strip-metadata"];
        for (var round = 0; round < 6; round++)
        {
            var folder = _dir.Combine($"round {round}");
            Directory.CreateDirectory(folder);
            var inputs = operations.Select((_, i) => Path.Combine(folder, $"in{i}.jpg")).ToArray();
            foreach (var input in inputs)
            {
                using var image = new MagickImage(MagickColors.Orange, 12, 8);
                image.Write(input, MagickFormat.Jpeg);
            }

            MakePolicyStale();
            var processes = operations.Select((operation, i) => StartHeadless(operation, inputs[i])).ToArray();
            try
            {
                await Task.WhenAll(processes.Select(p => p.WaitForExitAsync())).WaitAsync(TimeSpan.FromSeconds(60));
                Assert.True(processes.All(p => p.ExitCode == 0),
                    $"round {round}: " + string.Join('\n', Directory.GetFiles(_logs.Path).Select(File.ReadAllText)));
            }
            finally
            {
                foreach (var process in processes)
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    process.Dispose();
                }
            }
        }

        var policy = Path.Combine(MagickSetup.ConfigurationDirectory, "policy.xml");
        Assert.DoesNotContain("stale", File.ReadAllText(policy), StringComparison.Ordinal);
    }

    private Process StartHeadless(string operation, string input)
    {
        var start = new ProcessStartInfo(FlevertExe) { UseShellExecute = false };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(operation);
        start.ArgumentList.Add(input);
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        start.Environment["FLEVERT_HEADLESS"] = "1";
        return Process.Start(start)!;
    }

    /// <summary>Same rules plus a comment: harmless if anything loads it, but not byte-for-byte Flevert's.</summary>
    private static void MakePolicyStale()
    {
        var policy = Path.Combine(MagickSetup.ConfigurationDirectory, "policy.xml");
        var stale = File.ReadAllText(policy).Replace("<!-- stale -->", "", StringComparison.Ordinal) + "<!-- stale -->";
        var temp = policy + ".stale-test.tmp";
        File.WriteAllText(temp, stale);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, policy, overwrite: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 100)
            {
                Thread.Sleep(20); // another test process (other worktrees, CI) may be reading it
            }
            catch (IOException) when (attempt < 100)
            {
                Thread.Sleep(20);
            }
        }
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
