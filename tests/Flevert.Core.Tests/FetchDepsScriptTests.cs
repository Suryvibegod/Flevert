using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
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
    public void A_kept_file_deleted_from_a_folder_that_remains_is_fetched_again()
    {
        // The build fails with "run fetch-deps" when pdfium.dll alone is gone; fetch-deps must not answer "up to date".
        var archive = Zip(("bin/tool.exe", "exe"), ("bin/tool.dll", "dll"));
        var manifest = Manifest(Item("tool", archive, Sha256(archive), kind: "zip", keep: ["bin/tool.exe", "bin/tool.dll"]));
        var deps = _dir.Combine("deps");
        Assert.Equal(0, Fetch(manifest, deps).ExitCode);

        File.Delete(Path.Combine(deps, @"tool\bin\tool.dll"));
        var again = Fetch(manifest, deps);

        Assert.True(again.ExitCode == 0, again.Output);
        Assert.Equal("dll", File.ReadAllText(Path.Combine(deps, @"tool\bin\tool.dll")));
    }

    [Fact]
    public void A_kept_file_changed_after_the_fetch_is_fetched_again()
    {
        var archive = Zip(("bin/tool.exe", "exe"));
        var manifest = Manifest(Item("tool", archive, Sha256(archive), kind: "zip", keep: ["bin/tool.exe"]));
        var deps = _dir.Combine("deps");
        Assert.Equal(0, Fetch(manifest, deps).ExitCode);

        File.WriteAllText(Path.Combine(deps, @"tool\bin\tool.exe"), "replaced");
        var again = Fetch(manifest, deps);

        Assert.True(again.ExitCode == 0, again.Output);
        Assert.Equal("exe", File.ReadAllText(Path.Combine(deps, @"tool\bin\tool.exe")));
    }

    [Fact]
    public void A_file_added_beside_the_kept_files_is_removed_by_fetching_again()
    {
        // Flevert.Pdf copies every DLL in deps\qpdf\bin next to flevert.exe, where qpdf.exe would load one planted there.
        var archive = Zip(("bin/tool.exe", "exe"));
        var manifest = Manifest(Item("tool", archive, Sha256(archive), kind: "zip", keep: ["bin/tool.exe"]));
        var deps = _dir.Combine("deps");
        Assert.Equal(0, Fetch(manifest, deps).ExitCode);

        File.WriteAllText(Path.Combine(deps, @"tool\bin\version.dll"), "planted");
        var again = Fetch(manifest, deps);

        Assert.True(again.ExitCode == 0, again.Output);
        Assert.Equal([@"bin\tool.exe"], Files(Path.Combine(deps, "tool")));
    }

    [Fact]
    public async Task Downloads_are_checked_and_unpacked_in_a_folder_only_this_user_and_administrators_can_change()
    {
        // On the maintainer's machine %TEMP% grants a sandbox account Modify, inherited by everything created there: it
        // could swap a download after its hash check, or a staged file before the copy into deps\. BUILTIN\Users plays
        // that account here. The download comes from a loopback server, which looks at the work folder mid-download.
        var temp = Directory.CreateDirectory(_dir.Combine("temp"));
        var tempAcl = temp.GetAccessControl();
        tempAcl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        temp.SetAccessControl(tempAcl);
        var archive = File.ReadAllBytes(Zip(("bin/tool.exe", "exe")));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/tool.zip";
        var manifest = Manifest(Item("tool", url, Convert.ToHexStringLower(SHA256.HashData(archive)), kind: "zip", keep: ["bin/tool.exe"]));
        var seen = new List<(string Name, bool Protected, string[] Others)>();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            using var request = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await request.ReadLineAsync())) { }
            seen.AddRange(WorkFolders(temp.FullName));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {archive.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(archive);
        });

        var run = Fetch(manifest, _dir.Combine("deps"), temp.FullName);
        listener.Stop();
        await server.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(run.ExitCode == 0, run.Output);
        var work = Assert.Single(seen);
        Assert.True(work.Protected, $"{work.Name} inherits entries from %TEMP%");
        Assert.Empty(work.Others);
        Assert.Empty(Directory.GetDirectories(temp.FullName, "flevert-deps-*")); // and it is gone afterwards
    }

    [Fact]
    public void Fetching_again_removes_a_junction_in_deps_without_touching_what_it_points_to()
    {
        // deps\ lies in the checkout, which other accounts may be able to change (setup-dev.ps1 warns): the old copy is
        // removed without following links. Windows PowerShell 5.1.26100's Remove-Item did not follow this junction
        // either (probed), but older builds and symbolic links were not checked.
        var archive = Zip(("bin/tool.exe", "exe"));
        var manifest = Manifest(Item("tool", archive, Sha256(archive), kind: "zip", keep: ["bin/tool.exe"]));
        var deps = _dir.Combine("deps");
        Assert.Equal(0, Fetch(manifest, deps).ExitCode);
        var outside = Directory.CreateDirectory(_dir.Combine("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "someone's file");
        Junction(Path.Combine(deps, @"tool\bin\link"), outside);

        var again = Fetch(manifest, deps);

        Assert.True(again.ExitCode == 0, again.Output);
        Assert.Equal("someone's file", File.ReadAllText(Path.Combine(outside, "precious.txt")));
        Assert.Equal([@"bin\tool.exe"], Files(Path.Combine(deps, "tool")));
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

    /// <param name="source">A local file, or a URL.</param>
    private static object Item(string name, string source, string sha256, string kind, string strip = "", string[]? keep = null, string? target = null) =>
        new { name, url = new Uri(source).AbsoluteUri, sha256, kind, strip, keep = keep ?? [], target };

    private string Manifest(params object[] items)
    {
        var path = _dir.Combine($"deps-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { items }));
        return path;
    }

    private static string[] Files(string dir) =>
        [.. Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)).Order(StringComparer.Ordinal)];

    private static (int ExitCode, string Output) Fetch(string manifest, string deps, string? temp = null) =>
        PowerShellScript.Run(
            "fetch-deps.ps1", ["-Manifest", manifest, "-Destination", deps],
            temp is null ? null : new Dictionary<string, string> { ["TEMP"] = temp, ["TMP"] = temp });

    // Each work folder fetch-deps.ps1 has in this temporary folder: whether it ignores the entries %TEMP% passes down, and
    // who other than this user, SYSTEM and Administrators it grants anything.
    private static IEnumerable<(string Name, bool Protected, string[] Others)> WorkFolders(string temp)
    {
        using var me = WindowsIdentity.GetCurrent();
        var trusted = new[] { me.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        foreach (var folder in Directory.GetDirectories(temp, "flevert-deps-*"))
        {
            var acl = new DirectoryInfo(folder).GetAccessControl();
            var others = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow && !trusted.Contains((SecurityIdentifier)rule.IdentityReference))
                .Select(rule => $"{rule.IdentityReference}: {rule.FileSystemRights}")
                .ToArray();
            yield return (Path.GetFileName(folder), acl.AreAccessRulesProtected, others);
        }
    }

    private static void Junction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }
}
