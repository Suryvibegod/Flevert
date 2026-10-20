using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flevert.Core.Tests;

/// <summary>
/// scripts/write-notices.ps1 writes THIRD-PARTY-NOTICES.txt for the installer, which packs the whole publish folder. A
/// file in that folder without a licence notice must stop the build, not ship: the NuGet packages are covered through
/// flevert.deps.json, everything else (pdfium, qpdf, the MSVC runtime) through the "notices" list in scripts/deps.json.
/// </summary>
public sealed class NoticesScriptTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _publish;
    private readonly string _packages;
    private readonly string _root;

    public NoticesScriptTests()
    {
        _publish = Directory.CreateDirectory(_dir.Combine("publish")).FullName;
        _packages = Directory.CreateDirectory(_dir.Combine("packages")).FullName;
        _root = Directory.CreateDirectory(_dir.Combine("repo")).FullName;

        // A publish folder as dotnet publish leaves it: one NuGet package (with a satellite assembly), Flevert's own
        // project, its host and settings files, and a symbol file the installer leaves out.
        Package("Some.Package", "1.2.3", "LICENSE.TXT", "Some Package licence text");
        File.WriteAllText(Path.Combine(_publish, "flevert.deps.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["runtimeTarget"] = new { name = ".NETCoreApp,Version=v10.0/win-x64" },
            ["targets"] = new Dictionary<string, object>
            {
                [".NETCoreApp,Version=v10.0/win-x64"] = new Dictionary<string, object>
                {
                    ["flevert/1.0.0"] = new { runtime = new Dictionary<string, object> { ["flevert.dll"] = new { } } },
                    ["Some.Package/1.2.3"] = new
                    {
                        runtime = new Dictionary<string, object> { ["lib/net8.0/Some.Package.dll"] = new { } },
                        native = new Dictionary<string, object> { ["runtimes/win-x64/native/Some.Native.dll"] = new { } },
                        resources = new Dictionary<string, object> { ["lib/net8.0/cs/Some.Package.resources.dll"] = new { locale = "cs" } },
                    },
                },
            },
            ["libraries"] = new Dictionary<string, object>
            {
                ["flevert/1.0.0"] = new { type = "project" },
                ["Some.Package/1.2.3"] = new { type = "package" },
            },
        }));
        foreach (var name in new[] { "flevert.exe", "flevert.dll", "flevert.pdb", "flevert.runtimeconfig.json", "Some.Package.dll", "Some.Native.dll", @"cs\Some.Package.resources.dll" })
            Write(Path.Combine(_publish, name), "binary");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_publish_folder_holding_only_package_and_flevert_files_gets_the_package_notices()
    {
        var run = Notices(Manifest());

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("Some Package licence text", NoticesText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_native_binary_without_a_notices_entry_stops_the_build_and_is_named()
    {
        Write(Path.Combine(_publish, "tool.dll"), "native code from somewhere");

        var run = Notices(Manifest());

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("tool.dll", run.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_publish, "THIRD-PARTY-NOTICES.txt")), "notices written for a build that must stop");
    }

    [Fact]
    public void A_hidden_binary_without_a_notices_entry_stops_the_build_too()
    {
        // Inno packs hidden and system files like any other.
        var hidden = Path.Combine(_publish, "tool.dll");
        Write(hidden, "native code from somewhere");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.System);

        var run = Notices(Manifest());

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("tool.dll", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hidden_licence_file_still_counts()
    {
        Write(Path.Combine(_publish, "tool.exe"), "binary");
        var licence = Path.Combine(_root, @"deps\tool\LICENSE.txt");
        Write(licence, "Tool licence text");
        File.SetAttributes(licence, FileAttributes.Hidden);

        var run = Notices(Manifest(Entry("Tool 1.0", ["tool.exe"], ["deps/tool/*"])));

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("Tool licence text", NoticesText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_binary_with_a_notices_entry_ships_with_every_licence_the_entry_names()
    {
        Write(Path.Combine(_publish, "tool.exe"), "binary");
        Write(Path.Combine(_publish, "tool.dll"), "binary");
        Write(Path.Combine(_root, @"deps\tool\LICENSE.txt"), "Tool licence text");
        Write(Path.Combine(_root, @"deps\tool\licenses\zlib.txt"), "zlib licence text");
        Write(Path.Combine(_root, @"installer\notices\runtime.txt"), "Runtime terms text");

        var run = Notices(Manifest(
            Entry("Tool 1.0", ["tool.exe"], ["deps/tool/LICENSE.txt", "deps/tool/licenses/*"], note: "Tool links zlib."),
            Entry("Tool runtime", ["tool.dll"], ["installer/notices/runtime.txt"])));

        Assert.True(run.ExitCode == 0, run.Output);
        var notices = NoticesText();
        Assert.Contains("Tool 1.0", notices, StringComparison.Ordinal);
        Assert.Contains("Tool links zlib.", notices, StringComparison.Ordinal);
        Assert.Contains("Tool licence text", notices, StringComparison.Ordinal);
        Assert.Contains("zlib licence text", notices, StringComparison.Ordinal);
        Assert.Contains("Runtime terms text", notices, StringComparison.Ordinal);
        Assert.Contains("Some Package licence text", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void A_notices_entry_whose_licence_file_is_missing_stops_the_build()
    {
        Write(Path.Combine(_publish, "tool.exe"), "binary");

        var run = Notices(Manifest(Entry("Tool 1.0", ["tool.exe"], ["deps/tool/LICENSE.txt"])));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("deps/tool/LICENSE.txt", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_notices_entry_for_a_file_the_build_does_not_ship_stops_the_build()
    {
        // The list must describe what ships: an entry left over after an update would hide a renamed binary.
        Write(Path.Combine(_root, @"deps\tool\LICENSE.txt"), "Tool licence text");

        var run = Notices(Manifest(Entry("Tool 1.0", ["tool.exe"], ["deps/tool/LICENSE.txt"])));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("tool.exe", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_real_manifest_gives_every_binary_fetch_deps_keeps_its_licences()
    {
        // CI never builds the installer, so check here what a release would refuse: the files Flevert.Pdf.csproj copies
        // next to flevert.exe (deps\pdfium\bin\pdfium.dll, deps\qpdf\bin\*.exe and *.dll) against the real deps.json
        // and the licence files fetch-deps.ps1 put in the real deps\.
        var deps = Path.Combine(InstallerScriptTests.RepositoryRoot(), "deps");
        var shipped = Directory.GetFiles(Path.Combine(deps, @"pdfium\bin"), "pdfium.dll")
            .Concat(Directory.GetFiles(Path.Combine(deps, @"qpdf\bin"), "*.exe"))
            .Concat(Directory.GetFiles(Path.Combine(deps, @"qpdf\bin"), "*.dll"))
            .ToList();
        Assert.True(shipped.Count >= 3, "deps\\ is incomplete: run scripts\\fetch-deps.ps1");
        foreach (var file in shipped)
            Write(Path.Combine(_publish, Path.GetFileName(file)), "binary");

        var run = PowerShellScript.Run("write-notices.ps1", ["-Publish", _publish, "-Packages", _packages]);

        Assert.True(run.ExitCode == 0, run.Output);
        var notices = NoticesText();
        Assert.All(RealNoticeTexts, text => Assert.Contains(text, notices, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_file_a_real_publish_of_flevert_ships_has_its_notice()
    {
        // Final audit of v0.2.0: openssl.cnf, which Flevert.Pdf.csproj copies into the build, stopped
        // build-installer.ps1 while the faked publish folders above all passed. CI never builds the installer, so publish
        // flevert.exe for real (without ReadyToRun, which changes no file name) and run the script on what comes out.
        var publish = _dir.Combine("real publish");
        var dotnet = Dotnet();
        var published = Run(dotnet, ["publish", Path.Combine(InstallerScriptTests.RepositoryRoot(), "src", "Flevert.Cli"),
            "-c", "Release", "-r", "win-x64", "--self-contained", "-o", publish, "-nodeReuse:false", "-p:UseSharedCompilation=false"]);
        Assert.True(published.ExitCode == 0, published.Output);
        var locals = Run(dotnet, ["nuget", "locals", "global-packages", "--list"]);
        var packages = Regex.Match(locals.Output, @"^global-packages:\s*(.+?)\s*$", RegexOptions.Multiline).Groups[1].Value;
        Assert.True(Directory.Exists(packages), locals.Output);

        var run = PowerShellScript.Run("write-notices.ps1", ["-Publish", publish, "-Packages", packages]);

        Assert.True(run.ExitCode == 0, run.Output);
        var notices = File.ReadAllText(Path.Combine(publish, "THIRD-PARTY-NOTICES.txt"));
        Assert.All(RealNoticeTexts, text => Assert.Contains(text, notices, StringComparison.Ordinal));
    }

    /// <summary>The dotnet that runs these tests: DOTNET_ROOT, else PATH, else the per-user and machine-wide installs.</summary>
    private static string Dotnet()
    {
        var folders = new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        var found = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.Combine(f!.Trim('"'), "dotnet.exe")).FirstOrDefault(File.Exists);
        Assert.True(found is not null, "dotnet.exe not found");
        return found!;
    }

    private static (int ExitCode, string Output) Run(string program, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{program} {string.Join(' ', start.ArgumentList)} did not finish in 10 minutes");
        }

        return (process.ExitCode, output.Result + error.Result);
    }

    private static readonly string[] RealNoticeTexts =
    [
        "Benoit Blanchon", // pdfium-binaries
        "The PDFium Authors",
        "Jay Berkenbilt", // qpdf's NOTICE
        "Apache License",
        "OpenSSL 3",
        "Jean-loup Gailly", // zlib
        "This software is based in part on the work of the Independent JPEG Group", // the IJG licence asks for this sentence
        "Microsoft Visual C++",
    ];

    private void Package(string id, string version, string licenceName, string text) =>
        Write(Path.Combine(_packages, id.ToLowerInvariant(), version, licenceName), text);

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static object Entry(string title, string[] files, string[] licences, string? note = null) =>
        note is null ? new { title, files, licences } : new { title, files, licences, note };

    private string Manifest(params object[] notices)
    {
        var path = _dir.Combine($"deps-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { items = Array.Empty<object>(), notices }));
        return path;
    }

    private string NoticesText() => File.ReadAllText(Path.Combine(_publish, "THIRD-PARTY-NOTICES.txt"));

    private (int ExitCode, string Output) Notices(string manifest) =>
        PowerShellScript.Run("write-notices.ps1", ["-Publish", _publish, "-Packages", _packages, "-Manifest", manifest, "-Root", _root]);
}
