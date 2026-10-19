using Flevert.Core.Operations;

namespace Flevert.Core.Tests;

public sealed class LongPathTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Handles_paths_longer_than_260_characters()
    {
        var deep = _dir.Path;
        while (deep.Length < 300)
            deep = Path.Combine(deep, "thư mục rất dài " + new string('x', 40));
        Directory.CreateDirectory(deep);
        var source = Path.Combine(deep, "ảnh.txt");
        await File.WriteAllTextAsync(source, "deep");

        var output = await new DevCopy().RunAsync(source, CancellationToken.None);

        Assert.True(output.Length > 300);
        Assert.Equal("deep", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task Handles_emoji_and_vietnamese_file_names()
    {
        var source = _dir.File("Ảnh Đà Lạt 🍊.txt", "cam");

        var output = await new DevCopy().RunAsync(source, CancellationToken.None);

        Assert.Equal("Ảnh Đà Lạt 🍊 (copy).txt", Path.GetFileName(output));
    }

    [SkippableFact]
    public void Expands_8dot3_short_paths_that_Explorer_passes_for_long_paths()
    {
        var full = _dir.File("Ảnh Đà Lạt 🍊 rất dài.txt");
        var shortPath = ShortPath(full);
        Skip.If(shortPath == full, "8.3 short names are disabled on this volume.");

        Assert.Equal(full, InputPath.ToLongPath(shortPath));
    }

    [Fact]
    public void Leaves_missing_paths_unchanged()
    {
        var missing = _dir.Combine("NOFILE~1.TXT");

        Assert.Equal(missing, InputPath.ToLongPath(missing));
    }

    private static string ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, buffer.Length);
        return length == 0 ? path : new string(buffer, 0, length);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, char[] shortPath, int bufferLength);

    [Fact]
    public async Task Handles_file_names_near_the_255_character_limit()
    {
        // " (copy).txt" must still fit, so leave room for it in the source name.
        var source = _dir.File(new string('a', 240) + ".txt", "long");

        var output = await new DevCopy().RunAsync(source, CancellationToken.None);

        Assert.Equal("long", await File.ReadAllTextAsync(output));
    }
}
