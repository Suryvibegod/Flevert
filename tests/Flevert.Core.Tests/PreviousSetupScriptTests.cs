using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Flevert.Core.Tests;

/// <summary>
/// scripts/get-previous-setup.ps1 hands test-installer.ps1 the previous release's setup to install before upgrading over
/// it. That file is run, so it must be the pinned one, checked in a folder other accounts cannot change.
/// </summary>
public sealed class PreviousSetupScriptTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_setup_with_the_pinned_hash_is_copied_into_a_new_private_folder()
    {
        var given = _dir.File("Flevert-Setup-1.0.0.exe", "pretend setup");
        var destination = _dir.Combine("previous");

        var run = Get(given, destination, Sha256(given));

        Assert.True(run.ExitCode == 0, run.Output);
        var copy = Path.Combine(destination, "Flevert-Setup-1.0.0.exe");
        // Windows PowerShell prints in the console's code page, which mangles the test folder's Vietnamese name.
        Assert.EndsWith(@"\previous\Flevert-Setup-1.0.0.exe", run.Output.Trim().Split('\n')[^1].Trim(), StringComparison.Ordinal);
        Assert.Equal("pretend setup", File.ReadAllText(copy));
        var acl = new DirectoryInfo(destination).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected, "the folder inherits entries from %TEMP%");
        var trusted = new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") };
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => Assert.Contains((SecurityIdentifier)rule.IdentityReference, trusted));
    }

    [Fact]
    public void A_setup_whose_hash_differs_is_refused_and_nothing_is_left()
    {
        var given = _dir.File("Flevert-Setup-1.0.0.exe", "something else");
        var destination = _dir.Combine("previous");

        // The hash of other content than the file's.
        var run = Get(given, destination, Convert.ToHexStringLower(SHA256.HashData("pretend setup"u8)));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("SHA256", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination), "the refused copy was left behind");
    }

    [Fact]
    public void A_destination_that_exists_already_is_refused_and_left_alone()
    {
        // Someone else's folder could hold a planted file, or let them swap the setup after the check.
        var given = _dir.File("Flevert-Setup-1.0.0.exe", "pretend setup");
        var destination = Directory.CreateDirectory(_dir.Combine("previous")).FullName;
        File.WriteAllText(Path.Combine(destination, "theirs.txt"), "not ours");

        var run = Get(given, destination, Sha256(given));

        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(destination, "theirs.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "Flevert-Setup-1.0.0.exe")));
    }

    [Fact]
    public void Without_a_pinned_release_nothing_is_fetched_and_the_upgrade_scenario_is_told_so()
    {
        // 0.2.0 is the first release: until one is pinned, there is nothing to upgrade from.
        var given = _dir.File("Flevert-Setup-1.0.0.exe", "pretend setup");
        var destination = _dir.Combine("previous");

        var run = PowerShellScript.Run("get-previous-setup.ps1", ["-Path", given, "-Destination", destination]);
        var pinned = PowerShellScript.Run("get-previous-setup.ps1", ["-IsPinned"]);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("No previous release is pinned yet", run.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
        Assert.Equal("False", pinned.Output.Trim());
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static (int ExitCode, string Output) Get(string path, string destination, string? sha256 = null) =>
        PowerShellScript.Run("get-previous-setup.ps1",
            ["-Path", path, "-Destination", destination, "-Version", "1.0.0", .. sha256 is null ? Array.Empty<string>() : ["-Sha256", sha256]]);
}
