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
        Assert.Contains("'{#AppName}.Menu'", Script, StringComparison.Ordinal);
        Assert.Contains($@"'\{MenuInstaller.Associations}\'", Script, StringComparison.Ordinal);
        Assert.Contains(@"'\shell\{#AppName}'", Script, StringComparison.Ordinal);
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
