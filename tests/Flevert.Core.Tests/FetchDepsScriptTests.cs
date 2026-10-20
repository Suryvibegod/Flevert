using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Flevert.Core.Tests;

/// <summary>
/// scripts/fetch-deps.ps1 is the only way pdfium and qpdf reach a build: it must check every download against its pinned
/// hash before unpacking anything, keep only the listed files, and leave deps/ untouched on any failure.
/// </summary>
public sealed class FetchDepsScriptTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void The_real_manifest_pins_every_download_by_sha256_over_https()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(InstallerScriptTests.RepositoryRoot(), "scripts", "deps.json")));

        var items = manifest.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.StartsWith("https://", item.GetProperty("url").GetString(), StringComparison.Ordinal);
            Assert.Matches("^[0-9a-f]{64}$", item.GetProperty("sha256").GetString());
        });
    }

    [Fact]
    public void A_download_with_the_pinned_hash_is_unpacked_keeping_only_the_listed_files()
    {
        var archive = Zip(("tool-1.0/bin/tool.exe", "exe"), ("tool-1.0/bin/tool.dll", "dll"), ("tool-1.0/include/tool.h", "h"));
        var licence = _dir.File("LICENSE.txt", "licence text");
        var manifest = Manifest(
            Item("tool", archive, Sha256(archive), kind: "zip", strip: "tool-1.0/", keep: ["bin/tool.exe", "bin/*.dll"]),
            Item("tool-licence", licence, Sha256(licence), kind: "file", target: "tool/LICENSE.txt"));
        var deps = _dir.Combine("deps");

        var run = Fetch(manifest, deps);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(["LICENSE.txt", @"bin\tool.dll", @"bin\tool.exe"], Files(Path.Combine(deps, "tool")));
        Assert.Equal("exe", File.ReadAllText(Path.Combine(deps, @"tool\bin\tool.exe")));
    }

    [Fact]
    public void A_download_whose_hash_differs_fails_and_changes_nothing()
    {
        var archive = Zip(("bin/tool.exe", "exe"));
        var deps = _dir.Combine("deps");
        Directory.CreateDirectory(Path.Combine(deps, "tool"));
        File.WriteAllText(Path.Combine(deps, @"tool\old.txt"), "from an earlier fetch");
        var manifest = Manifest(Item("tool", archive, new string('0', 64), kind: "zip", keep: ["bin/tool.exe"]));

        var run = Fetch(manifest, deps);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("SHA256", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["old.txt"], Files(Path.Combine(deps, "tool")));
    }

    [Fact]
    public void A_second_run_with_nothing_changed_downloads_nothing()
    {
        var archive = Zip(("bin/tool.exe", "exe"));
        var manifest = Manifest(Item("tool", archive, Sha256(archive), kind: "zip", keep: ["bin/tool.exe"]));
        var deps = _dir.Combine("deps");
        Assert.Equal(0, Fetch(manifest, deps).ExitCode);

        File.Delete(archive); // a second download would now fail
        var again = Fetch(manifest, deps);

        Assert.True(again.ExitCode == 0, again.Output);
        Assert.True(File.Exists(Path.Combine(deps, @"tool\bin\tool.exe")));
    }

    [Fact]
    public void A_deleted_dependency_is_fetched_again()
    {
        var archive = Zip(("bin/tool.exe", "exe"));
        var manifest = Manifest(Item("tool", archive, Sha256(archive), kind: "zip", keep: ["bin/tool.exe"]));
        var deps = _dir.Combine("deps");
        Assert.Equal(0, Fetch(manifest, deps).ExitCode);

        Directory.Delete(Path.Combine(deps, "tool"), recursive: true);
        var again = Fetch(manifest, deps);

        Assert.True(again.ExitCode == 0, again.Output);
        Assert.True(File.Exists(Path.Combine(deps, @"tool\bin\tool.exe")));
    }

    [Fact]
    public void A_listed_file_missing_from_the_archive_fails()
    {
        var archive = Zip(("bin/tool.exe", "exe"));
        var manifest = Manifest(Item("tool", archive, Sha256(archive), kind: "zip", keep: ["bin/tool.exe", "bin/missing.dll"]));

        var run = Fetch(manifest, _dir.Combine("deps"));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("missing.dll", run.Output, StringComparison.Ordinal);
    }

    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = _dir.Combine($"archive-{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static object Item(string name, string path, string sha256, string kind, string strip = "", string[]? keep = null, string? target = null) =>
        new { name, url = new Uri(path).AbsoluteUri, sha256, kind, strip, keep = keep ?? [], target };

    private string Manifest(params object[] items)
    {
        var path = _dir.Combine($"deps-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { items }));
        return path;
    }

    private static string[] Files(string dir) =>
        [.. Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)).Order(StringComparer.Ordinal)];

    private static (int ExitCode, string Output) Fetch(string manifest, string deps) =>
        PowerShellScript.Run("fetch-deps.ps1", ["-Manifest", manifest, "-Destination", deps]);
}
