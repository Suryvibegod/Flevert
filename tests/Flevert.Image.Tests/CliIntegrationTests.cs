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
    public async Task Ca_converts_a_jpg_to_png()
    {
        var input = _dir.Combine("ảnh.jpg");
        using (var image = new MagickImage(MagickColors.Orange, 12, 8))
            image.Write(input, MagickFormat.Jpeg);

        var start = new ProcessStartInfo(CaExe) { UseShellExecute = false };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("image.to-png");
        start.ArgumentList.Add(input);
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        start.Environment["FLEVERT_HEADLESS"] = "1";
        using var ca = Process.Start(start)!;
        try
        {
            await ca.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!ca.HasExited)
                ca.Kill(entireProcessTree: true);
        }

        Assert.True(ca.ExitCode == 0, string.Join('\n', Directory.GetFiles(_logs.Path).Select(File.ReadAllText)));
        using var png = new MagickImage(_dir.Combine("ảnh.png"));
        Assert.Equal(MagickFormat.Png, png.Format);
    }

    private static string CaExe
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
