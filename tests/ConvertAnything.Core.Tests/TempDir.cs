namespace ConvertAnything.Core.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ca-tests", $"Ảnh Đà Lạt {Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name, string content = "x")
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public string[] FileNames() =>
        Directory.GetFiles(Path).Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
