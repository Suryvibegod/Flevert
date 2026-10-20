using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flevert.Core.Menu;

namespace Flevert.Core.Tests;

/// <summary>
/// The uninstaller's Pascal safety net repeats the registry names used by MenuInstaller and ClassicMenu. Only the
/// end-to-end installer test would notice them drifting apart, so check the script text on every test run.
/// </summary>
public sealed class InstallerScriptTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Flevert.iss"));

    [Fact]
    public void Menu_key_names_match_the_menu_installer()
    {
        Assert.Contains($"#define AppName \"{MenuInstaller.Title}\"", Script, StringComparison.Ordinal);
        Assert.Contains("SweepMenu('{#AppName}')", Script, StringComparison.Ordinal);
        Assert.Contains("Name + '.Menu'", Script, StringComparison.Ordinal);
        Assert.Contains($@"'\{MenuInstaller.Associations}\'", Script, StringComparison.Ordinal);
        Assert.Contains(@"'\shell\' + Name", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_moves_a_flevert_install_over()
    {
        // ADR-0009: Flevert 0.1.0 was released as Flevert, with the same AppId.
        Assert.Contains("#define OldName \"Flevert\"", Script, StringComparison.Ordinal);
        Assert.Contains("AppMutex={#AppMutexName},{#OldName}.App", Script, StringComparison.Ordinal);
        Assert.Contains("SweepMenu('{#OldName}')", Script, StringComparison.Ordinal);
        Assert.Contains("RegValueExists(HKCU, ClassicMenuKey, '{#OldName}')", Script, StringComparison.Ordinal);
        Assert.Contains(@"'{localappdata}\{#OldName}\update\state.json'", Script, StringComparison.Ordinal);
        foreach (var folder in AppData.Subfolders)
            Assert.Contains($@"DeleteOldFolder(OldData + '\{folder}');", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Classic_menu_key_and_marker_match_the_classic_menu()
    {
        Assert.Contains($@"ClassicMenuKey = 'Software\Classes\{ClassicMenu.Key}';", Script, StringComparison.Ordinal);
        Assert.Contains($"ClassicMenuMarker = '{ClassicMenu.Marker}';", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_flevert_by_hand_deletes_every_folder_it_writes()
    {
        // The documented fallback for when Windows blocks the uninstaller must keep up with AppData.Subfolders.
        var guide = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "install.md"));
        var loop = Regex.Match(guide, @"for %d in \(([a-z ]+)\) do");
        Assert.True(loop.Success, "the by-hand removal loop over app data folders is missing");
        Assert.Equal(AppData.Subfolders.Order(StringComparer.Ordinal), loop.Groups[1].Value.Split(' ').Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_command_setup_runs_is_one_flevert_understands()
    {
        var commands = Regex.Matches(Script, @"RunFlevert\('([a-z-]+(?: [a-z]+)?)'").Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.NotEmpty(commands);
        Assert.All(commands, c => Assert.IsNotType<InvalidCommand>(CommandLine.Parse(c.Split(' '))));
    }

    [Fact]
    public void Setup_applies_the_update_check_choice_both_ways()
    {
        // Applied on every install, so unticking it on an upgrade turns the check off, and ticking it turns it back on.
        Assert.Contains("RunFlevert('update-check on'", Script, StringComparison.Ordinal);
        Assert.Contains("RunFlevert('update-check off'", Script, StringComparison.Ordinal);
        Assert.Contains("{param:UPDATECHECK|}", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_keeps_an_update_check_turned_off_with_the_command()
    {
        // An upgrade without /UPDATECHECK must not turn the check back on after update-check off: setup reads the state
        // file flevert.exe writes, by the same path and marker.
        Assert.Contains(@"'{localappdata}\{#AppName}\" + AppData.UpdateFolder + @"\state.json'", Script, StringComparison.Ordinal);
        Assert.Contains($"'{Flevert.Core.Updates.UpdateStateStore.TurnedOffMarker}'", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_waits_on_the_mutex_flevert_holds()
    {
        Assert.Contains($"#define AppMutexName \"{AppMutex.Name}\"", Script, StringComparison.Ordinal);
        Assert.Contains("AppMutex={#AppMutexName}", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_folder_check_looks_for_the_uninstall_entry_of_this_app_id()
    {
        // PrepareToInstall accepts a non-empty folder only over an earlier install, found by its uninstall entry.
        var appId = Regex.Match(Script, @"^AppId=\{\{([0-9A-F-]{36})\}\r?$", RegexOptions.Multiline);
        Assert.True(appId.Success, "AppId not found");
        Assert.Contains($@"\Uninstall\{{{appId.Groups[1].Value}}}_is1';", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Exit_codes_setup_tells_apart_match_flevert()
    {
        Assert.Contains($"ExitRefusedLocation = {ExitCodes.RefusedLocation};", Script, StringComparison.Ordinal);
        Assert.Contains($"(Code >= {ExitCodes.Failed}) and (Code <= {ExitCodes.RefusedLocation})", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_readme_link_in_the_script_names_a_readme_heading()
    {
        // Lines starting with # inside code blocks are comments, not headings.
        var readme = Regex.Replace(File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md")), @"(?ms)^```.*?^```", "");
        var anchors = Regex.Matches(readme, @"^#{1,6} (.+)$", RegexOptions.Multiline)
            .Select(m => Slug(m.Groups[1].Value.Trim())).ToHashSet(StringComparer.Ordinal);
        var links = Regex.Matches(Script, @"\{#AppUrl\}#([a-z0-9-]+)").Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.NotEmpty(links);
        Assert.All(links, link => Assert.Contains(link, anchors));
    }

    [Fact]
    public void Link_check_walks_up_to_the_drive_root_itself()
    {
        // "C:" without a backslash is the current directory on drive C, so plain RemoveBackslash on "C:\" checked
        // the wrong folder. Setup does not keep the caller's working directory, so no end-to-end test can show it.
        var body = Regex.Match(Script, @"(?ms)^function LinkOnPath\(.*?^end;").Value;

        Assert.Contains("RemoveBackslashUnlessRoot", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\bRemoveBackslash\(", body);
    }

    // GitHub's heading anchors: lower case, spaces to hyphens, punctuation dropped.
    private static string Slug(string heading) =>
        Regex.Replace(heading.ToLowerInvariant().Replace(' ', '-'), @"[^a-z0-9\-_]", "");

    [SkippableFact]
    public void The_installer_does_not_compile_from_a_folder_without_the_third_party_notices()
    {
        // build-installer.ps1 writes THIRD-PARTY-NOTICES.txt (write-notices.ps1) before compiling, but ISCC run by hand
        // on a plain dotnet publish folder would package pdfium, qpdf and the MSVC runtime without their licences.
        string[] candidates =
        [
            .. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(folder => Path.Combine(folder.Trim('"'), "ISCC.exe")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Inno Setup 6\ISCC.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Inno Setup 6\ISCC.exe"),
        ];
        // Only a compiler the script accepts: an older one stops at the script's own version check before anything else
        // (GitHub's Windows runners ship an older Inno Setup; release.yml installs 6.7.3 itself).
        var iscc = candidates.Where(File.Exists).FirstOrDefault(AcceptsScriptVersion);
        Skip.If(iscc is null, "Inno Setup 6.7.3 or later is not installed (scripts\\setup-dev.ps1 installs it).");
        using var dir = new TempDir();
        var source = Directory.CreateDirectory(dir.Combine("publish")).FullName;
        // Any program with a version resource stands in for flevert.exe, which the script reads its version from.
        File.Copy(Environment.ProcessPath!, Path.Combine(source, "flevert.exe"));
        // [Files] names each of these, and ISCC fails when one is missing.
        foreach (var file in ConversionOnlyFiles())
            File.WriteAllText(Path.Combine(source, file), "stand-in");

        var without = Compile(iscc!, source, dir.Combine("out"));
        File.WriteAllText(Path.Combine(source, "THIRD-PARTY-NOTICES.txt"), "notices");
        var with = Compile(iscc!, source, dir.Combine("out"));

        Assert.True(without.ExitCode != 0, without.Output);
        Assert.Contains("THIRD-PARTY-NOTICES.txt not found", without.Output, StringComparison.Ordinal);
        Assert.True(with.ExitCode == 0, with.Output);
    }

    private static (int ExitCode, string Output) Compile(string iscc, string source, string output)
    {
        var start = new ProcessStartInfo(iscc) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/Qp", $"/DSourceDir={source}", $"/O{output}", Path.Combine(RepositoryRoot(), "installer", "Flevert.iss") })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ISCC did not finish in 2 minutes");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    // What a conversion loads but flevert.exe check-location never does: pdfium, qpdf and its MSVC runtime (the files
    // listed under "notices" in scripts/deps.json), qpdf's OpenSSL configuration, and ImageMagick's native library.
    /// <summary>
    /// Whether this ISCC accepts installer/Flevert.iss's version check. ISCC carries no version resource and prints no
    /// version, so compile a script with the same check and no output (Output=no).
    /// </summary>
    private static bool AcceptsScriptVersion(string iscc)
    {
        using var dir = new TempDir();
        var probe = dir.Combine("probe.iss");
        File.WriteAllText(probe, """
            #if VER < EncodeVer(6, 7, 3)
              #error too old
            #endif
            [Setup]
            AppName=Probe
            AppVersion=1
            DefaultDirName=Probe
            Output=no

            """);
        var start = new ProcessStartInfo(iscc) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/Q");
        start.ArgumentList.Add(probe);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        return process.WaitForExit(60_000) && process.ExitCode == 0;
    }

    private static IEnumerable<string> ConversionOnlyFiles()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "deps.json")));
        var listed = manifest.RootElement.GetProperty("notices").EnumerateArray()
            .SelectMany(entry => entry.GetProperty("files").EnumerateArray().Select(file => file.GetString()!));
        return [.. listed.Distinct(StringComparer.OrdinalIgnoreCase), "openssl.cnf", "Magick.Native-Q8-x64.dll"];
    }

    // Some of what any start of flevert.exe loads: the runtime, Flevert's assemblies and Magick.NET's managed ones.
    private static readonly string[] StartUpFiles =
    [
        "flevert.exe", "flevert.dll", "flevert.deps.json", "flevert.runtimeconfig.json", "Flevert.Core.dll",
        "Flevert.Pdf.dll", "coreclr.dll", "hostfxr.dll", "System.Private.CoreLib.dll", "Magick.NET.Core.dll",
    ];

    [Fact]
    public void Setup_checks_the_install_folder_without_unpacking_what_only_a_conversion_loads()
    {
        // LocationProblem unpacks into setup's temporary folder the files flevert.exe check-location needs, and runs it
        // there. The engines and their runtime (about 40 MB) are only loaded by a conversion: they must not be unpacked
        // into %TEMP% and run from there, and each must still be installed once into the install folder.
        Assert.NotEmpty(TemporaryExtractPatterns());
        Assert.All(ConversionOnlyFiles(), file =>
        {
            Assert.True(InstalledByOneEntry(file), $"{file} is not installed by exactly one [Files] entry");
            Assert.False(UnpackedForTheCheck(file), $"{file} is unpacked for check-location");
        });
        Assert.All(StartUpFiles, file =>
        {
            Assert.True(InstalledByOneEntry(file), $"{file} is not installed by exactly one [Files] entry");
            Assert.True(UnpackedForTheCheck(file), $"{file} is not unpacked for check-location");
        });
        Assert.False(InstalledByOneEntry("flevert.pdb"), "symbol files are installed");
    }

    [Fact]
    public async Task Flevert_exe_runs_check_location_from_only_the_files_setup_unpacks_for_it()
    {
        // The other half of the test above: what is left out must not be needed. flevert.exe builds its whole catalog at
        // start, which loads Magick.NET's managed assemblies (seen failing without them) but none of the native engines.
        var build = Path.GetDirectoryName(BuiltFlevertExe())!;
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Combine("unpacked")).FullName;
        foreach (var file in Directory.EnumerateFiles(build, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!InstalledByOneEntry(name) || !UnpackedForTheCheck(name))
                continue;
            var target = Path.Combine(folder, Path.GetRelativePath(build, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        Assert.True(File.Exists(Path.Combine(build, "qpdf.exe")) && !File.Exists(Path.Combine(folder, "qpdf.exe")), "the copy should lack qpdf.exe");
        var start = new ProcessStartInfo(Path.Combine(folder, "flevert.exe")) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("check-location");
        start.ArgumentList.Add(dir.Combine("missing"));
        start.Environment["FLEVERT_LOG_DIR"] = Directory.CreateDirectory(dir.Combine("logs")).FullName;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        // "does not exist" comes from the command itself, after the start-up that needs the assemblies.
        Assert.True(process.ExitCode == ExitCodes.RefusedLocation, $"exit {process.ExitCode}: {stderr}{await stdout}");
        Assert.Contains("does not exist", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Setup_tells_how_to_reach_the_menu_from_a_picture_or_a_pdf()
    {
        // Since v0.2 the menu is on PDFs too; setup's last page and its failure text should not say pictures only.
        var hints = Regex.Matches(Script, @"Right-click a [^,']+").Select(m => m.Value).ToList();

        Assert.NotEmpty(hints);
        Assert.All(hints, hint => Assert.StartsWith("Right-click a picture or a PDF", hint, StringComparison.Ordinal));
    }

    [Fact]
    public void An_upgrade_removes_the_runtime_file_named_after_the_previous_runtime_version()
    {
        // The self-contained runtime ships mscordaccore_amd64_amd64_<runtime version>.dll beside mscordaccore.dll, and
        // each release moves to the newest .NET patch (RELEASING): Inno never deletes what an earlier version installed,
        // so every upgrade would leave the previous one behind. Deleted before the new files are copied.
        var section = Regex.Match(Script, @"(?ms)^\[InstallDelete\]\r?$(.*?)^\[").Groups[1].Value;

        Assert.Contains(@"Type: files; Name: ""{app}\mscordaccore_amd64_amd64_*.dll""", section, StringComparison.Ordinal);
    }

    private sealed record FilesEntry(string Source, string[] Excludes, string DestDir);

    private static List<FilesEntry> FilesEntries()
    {
        var section = Regex.Match(Script, @"(?ms)^\[Files\]\r?$(.*?)^\[").Groups[1].Value;
        var entries = new List<FilesEntry>();
        foreach (var line in section.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != ';'))
        {
            var values = Regex.Matches(line, @"(\w+): ""([^""]*)""").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            Assert.StartsWith(@"{#SourceDir}\", values["Source"], StringComparison.Ordinal);
            entries.Add(new FilesEntry(
                values["Source"][@"{#SourceDir}\".Length..],
                values.TryGetValue("Excludes", out var excludes) ? excludes.Split(',') : [],
                values["DestDir"]));
        }

        return entries;
    }

    private static List<FilesEntry> EntriesInstalling(string fileName) =>
        [.. FilesEntries().Where(e => Wildcard(e.Source, fileName) && !e.Excludes.Any(x => Wildcard(x, fileName)))];

    // Into the install folder, by one entry only: {app}, or the function that returns it.
    private static bool InstalledByOneEntry(string fileName)
    {
        var entries = EntriesInstalling(fileName);
        if (entries.Count != 1)
            return false;
        var destination = entries[0].DestDir;
        if (destination == "{app}")
            return true;
        var function = Regex.Match(destination, @"^\{code:(\w+)\}$");
        return function.Success && Regex.IsMatch(Script,
            $@"(?ms)^function {function.Groups[1].Value}\(Param: String\): String;\s*begin\s*Result := ExpandConstant\('\{{app\}}'\);\s*end;");
    }

    // ExtractTemporaryFiles matches its pattern against each entry's destination as written, constants unexpanded.
    private static bool UnpackedForTheCheck(string fileName) =>
        EntriesInstalling(fileName).Any(e => TemporaryExtractPatterns().Any(p => Wildcard(p, e.DestDir + @"\" + fileName)));

    private static List<string> TemporaryExtractPatterns() =>
        [.. Regex.Matches(Script, @"ExtractTemporaryFiles?\('([^']+)'\)").Select(m => m.Groups[1].Value)];

    private static bool Wildcard(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string BuiltFlevertExe()
    {
        // tests/<project>/bin/<Configuration>/<tfm>/ → the Flevert.Cli build of the same configuration.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var exe = Path.Combine(RepositoryRoot(), "src", "Flevert.Cli", "bin", output.Parent!.Name, output.Name, "flevert.exe");
        Assert.True(File.Exists(exe), $"Build the solution first; missing {exe}");
        return exe;
    }

    [Fact]
    public void Uninstall_removes_each_folder_flevert_writes_under_local_app_data()
    {
        Assert.Equal(MenuInstaller.Title, AppData.FolderName);
        foreach (var folder in AppData.Subfolders)
            Assert.Contains($@"Type: filesandordirs; Name: ""{{localappdata}}\{{#AppName}}\{folder}""", Script, StringComparison.Ordinal);
    }

    internal static string RepositoryRoot()
    {
        // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
    }
}
