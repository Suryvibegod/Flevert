using System.Text.RegularExpressions;
using ConvertAnything.Core.Menu;

namespace ConvertAnything.Core.Tests;

/// <summary>
/// The uninstaller's Pascal safety net repeats the registry names used by MenuInstaller and ClassicMenu. Only the
/// end-to-end installer test would notice them drifting apart, so check the script text on every test run.
/// </summary>
public sealed class InstallerScriptTests
{
    private static readonly string Script = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "ConvertAnything.iss"));

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
    public void Installer_waits_on_the_mutex_ca_holds()
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
    public void Uninstall_removes_each_folder_ca_writes_under_local_app_data()
    {
        Assert.Equal(MenuInstaller.Title, AppData.FolderName);
        foreach (var folder in AppData.Subfolders)
            Assert.Contains($@"Type: filesandordirs; Name: ""{{localappdata}}\{{#AppName}}\{folder}""", Script, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        // tests/<project>/bin/<Configuration>/<tfm>/ → repository root is five levels up.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
    }
}
