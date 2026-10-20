using System.Security.AccessControl;
using System.Security.Principal;

namespace Flevert.Core.Tests;

/// <summary>
/// scripts/check-checkout.ps1, run by setup-dev.ps1: a checkout other accounts can change lets them replace the binaries
/// in deps\ or add git hooks, which then run as the developer. It only warns and says how to fix it; it changes nothing.
/// </summary>
public sealed class CheckoutScriptTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_checkout_other_accounts_can_change_gets_a_warning_with_the_command_that_fixes_it()
    {
        // As on a second NTFS drive, whose root gives Authenticated Users Modify for everything created below it.
        var checkout = Folder("checkout", inheritFromParent: true);
        Grant(checkout, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify);

        var run = PowerShellScript.Run("check-checkout.ps1", ["-Path", checkout]);

        Assert.True(run.ExitCode == 0, run.Output); // a warning, not a failure: build and tests still run
        Assert.Contains(@"BUILTIN\Users", run.Output, StringComparison.Ordinal);
        // Windows PowerShell writes redirected output in the OEM code page, which mangles the Vietnamese part of the test
        // folder's name (a console shows it right): compare the end of the path.
        var pathEnd = checkout[(checkout.LastIndexOf(' ') + 1)..];
        Assert.Contains($"{pathEnd}\" /inheritance:r /grant:r", run.Output, StringComparison.Ordinal);
        using var me = WindowsIdentity.GetCurrent();
        Assert.Contains($"\"*{me.User}:(OI)(CI)F\" \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\"", run.Output, StringComparison.Ordinal);
        Assert.Equal(FileSystemRights.Modify, RightsOf(checkout, WellKnownSidType.BuiltinUsersSid)); // nothing changed
    }

    [Fact]
    public void A_checkout_only_this_user_SYSTEM_and_administrators_can_change_gets_no_warning()
    {
        var checkout = Folder("private", inheritFromParent: false);

        var run = PowerShellScript.Run("check-checkout.ps1", ["-Path", checkout]);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.DoesNotContain("icacls", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_access_for_other_accounts_is_no_reason_to_warn()
    {
        var checkout = Folder("readable", inheritFromParent: false);
        Grant(checkout, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute);

        var run = PowerShellScript.Run("check-checkout.ps1", ["-Path", checkout]);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.DoesNotContain("icacls", run.Output, StringComparison.Ordinal);
    }

    private string Folder(string name, bool inheritFromParent)
    {
        var folder = Directory.CreateDirectory(_dir.Combine(name));
        if (!inheritFromParent)
        {
            using var me = WindowsIdentity.GetCurrent();
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { me.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(acl);
        }

        return folder.FullName;
    }

    private static void Grant(string folder, SecurityIdentifier sid, FileSystemRights rights)
    {
        var info = new DirectoryInfo(folder);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    private static FileSystemRights RightsOf(string folder, WellKnownSidType type)
    {
        var sid = new SecurityIdentifier(type, null);
        return new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(sid) && !rule.IsInherited)
            .Aggregate((FileSystemRights)0, (all, rule) => all | rule.FileSystemRights) & ~FileSystemRights.Synchronize;
    }
}
