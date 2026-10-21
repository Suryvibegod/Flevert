using System.Diagnostics;

namespace Flevert.Core.Tests;

/// <summary>
/// install-menu and uninstall-menu on Linux: menu.json in Flevert's own folder and the extension where nautilus-python
/// loads extensions from; and the check that nobody else can change the folder flevert runs from, as InstallLocation
/// checks on Windows.
/// </summary>
public sealed class UnixMenuInstallerTests : IDisposable
{
    private const UnixFileMode Open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly TempDir _dir = new();

    // A base of its own, 0700 under the sticky /tmp: the shared test folder may be group-writable (umask 002), which
    // makes everything under it rightly unsafe.
    private readonly DirectoryInfo _private = Directory.CreateTempSubdirectory("flevert-location-");

    public void Dispose()
    {
        _dir.Dispose();
        _private.Delete(recursive: true);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Install_writes_the_menu_and_the_extension_and_uninstall_removes_both()
    {
        var installer = Installer();
        var extension = _dir.File("flevert_menu.py", "# the extension");

        installer.Install("""{"version":1}""", extension);

        Assert.Equal("""{"version":1}""", File.ReadAllText(_dir.Combine("data/menu.json")));
        Assert.Equal("# the extension", File.ReadAllText(_dir.Combine("extensions/flevert_menu.py")));
        Assert.Equal(UnixFileMode.None,
            File.GetUnixFileMode(_dir.Combine("extensions/flevert_menu.py")) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));

        installer.Uninstall();

        Assert.False(File.Exists(_dir.Combine("data/menu.json")));
        Assert.False(File.Exists(_dir.Combine("extensions/flevert_menu.py")));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Uninstall_removes_the_bytecode_python_cached_for_the_extension_and_nothing_else()
    {
        // Python caches what Nautilus imported beside the extension; another extension's cache stays.
        var installer = Installer();
        installer.Install("{}", _dir.File("flevert_menu.py", "# the extension"));
        var cache = Directory.CreateDirectory(_dir.Combine("extensions/__pycache__")).FullName;
        File.WriteAllText(Path.Combine(cache, "flevert_menu.cpython-310.pyc"), "");
        File.WriteAllText(Path.Combine(cache, "flevert_menu.cpython-312.opt-1.pyc"), "");
        File.WriteAllText(Path.Combine(cache, "theirs.cpython-310.pyc"), "");

        installer.Uninstall();

        Assert.Equal(["theirs.cpython-310.pyc"], Directory.GetFiles(cache).Select(Path.GetFileName));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Uninstall_leaves_no_empty_cache_folder_behind()
    {
        var installer = Installer();
        installer.Install("{}", _dir.File("flevert_menu.py", "# the extension"));
        var cache = Directory.CreateDirectory(_dir.Combine("extensions/__pycache__")).FullName;
        File.WriteAllText(Path.Combine(cache, "flevert_menu.cpython-310.pyc"), "");

        installer.Uninstall();

        Assert.False(Directory.Exists(cache));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Installing_again_replaces_a_link_put_in_place_of_the_menu_never_what_it_points_to()
    {
        var installer = Installer();
        var extension = _dir.File("flevert_menu.py", "# the extension");
        var theirs = _dir.File("theirs.json", "theirs");
        Directory.CreateDirectory(_dir.Combine("data"));
        File.CreateSymbolicLink(_dir.Combine("data/menu.json"), theirs);

        installer.Install("""{"version":1}""", extension);

        Assert.Equal("theirs", File.ReadAllText(theirs));
        Assert.Null(new FileInfo(_dir.Combine("data/menu.json")).LinkTarget);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_folder_only_this_user_can_change_is_a_safe_place_to_run_from()
    {
        var app = Directory.CreateDirectory(Path.Combine(_private.FullName, "app")).FullName;
        File.SetUnixFileMode(app, Open);

        Assert.True(UnixInstallLocation.IsWritableOnlyByOwner(app));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_folder_others_can_write_is_not()
    {
        var app = Directory.CreateDirectory(Path.Combine(_private.FullName, "app")).FullName;
        File.SetUnixFileMode(app, Open | UnixFileMode.OtherWrite);

        Assert.False(UnixInstallLocation.IsWritableOnlyByOwner(app));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_folder_whose_parent_others_can_write_is_not()
    {
        // Whoever can write the parent can rename the folder away and put another flevert in its place.
        var parent = Directory.CreateDirectory(Path.Combine(_private.FullName, "shared")).FullName;
        File.SetUnixFileMode(parent, Open | UnixFileMode.GroupWrite);
        var app = Directory.CreateDirectory(Path.Combine(parent, "app")).FullName;
        File.SetUnixFileMode(app, Open);

        Assert.False(UnixInstallLocation.IsWritableOnlyByOwner(app));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_parent_others_can_write_but_not_rename_in_is_fine()
    {
        // The sticky bit, as on /tmp: others can add their own entries but cannot rename or delete this user's.
        var parent = Directory.CreateDirectory(Path.Combine(_private.FullName, "sticky")).FullName;
        File.SetUnixFileMode(parent, Open | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.StickyBit);
        var app = Directory.CreateDirectory(Path.Combine(parent, "app")).FullName;
        File.SetUnixFileMode(app, Open);

        Assert.True(UnixInstallLocation.IsWritableOnlyByOwner(app));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_folder_reached_through_a_link_is_not()
    {
        var real = Directory.CreateDirectory(Path.Combine(_private.FullName, "real")).FullName;
        File.SetUnixFileMode(real, Open);
        var link = Path.Combine(_private.FullName, "link");
        Directory.CreateSymbolicLink(link, real);

        Assert.True(UnixInstallLocation.IsWritableOnlyByOwner(real), "control: the folder itself is safe");
        Assert.False(UnixInstallLocation.IsWritableOnlyByOwner(link));
    }

    [SupportedOSPlatform("linux")]
    private UnixMenuInstaller Installer() => new(_dir.Combine("data"), _dir.Combine("extensions"));

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Install_menu_refuses_a_flevert_others_can_replace_unless_told_otherwise()
    {
        // The flevert the tests run sits in the build output, in this user's checkout.
        var start = new ProcessStartInfo(BuiltFlevert.Path) { UseShellExecute = false, RedirectStandardError = true };
        start.ArgumentList.Add("install-menu");
        start.Environment["XDG_DATA_HOME"] = _dir.Path;
        var output = Path.GetDirectoryName(BuiltFlevert.Path)!;
        var before = File.GetUnixFileMode(output);
        try
        {
            File.SetUnixFileMode(output, before | UnixFileMode.OtherWrite);
            using var flevert = Process.Start(start)!;
            var error = flevert.StandardError.ReadToEnd();
            flevert.WaitForExit();

            Assert.Equal(ExitCodes.RefusedLocation, flevert.ExitCode);
            Assert.Contains("Refusing", error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(_dir.Path, "nautilus-python", "extensions", "flevert_menu.py")));
        }
        finally
        {
            File.SetUnixFileMode(output, before);
        }
    }
}
