using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Flevert.Core.Tests;

/// <summary>
/// scripts/check-checkout.ps1, run by setup-dev.ps1 and fetch-deps.ps1: a checkout other accounts can change lets them
/// replace the binaries in deps\ or add git hooks, which then run as the developer. It only warns and prints the
/// commands that fix it; it changes nothing. Each test runs the printed commands and checks again.
/// </summary>
public sealed class CheckoutScriptTests : IDisposable
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);

    // Plain ASCII: Windows PowerShell writes redirected output in the OEM code page, and the test runs the printed
    // commands. The walk up the parents stops at "flevert-tests", whose own parent %TEMP% may grant a sandbox access.
    private readonly string _stopAt = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "flevert-tests")).FullName;
    private readonly string _parent;

    public CheckoutScriptTests()
    {
        _parent = Path.Combine(_stopAt, $"checkout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_parent);
        SetPrivate(_parent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_checkout_that_inherits_write_access_for_other_accounts_is_reported_with_a_command_that_fixes_it()
    {
        // As on a second NTFS drive: its root gives Authenticated Users Modify on everything created below it, through an
        // inherit-only entry, so nothing in the checkout has an entry of its own.
        AddRule(_parent, Users, FileSystemRights.Modify, PropagationFlags.InheritOnly);
        var checkout = Directory.CreateDirectory(Path.Combine(_parent, "checkout")).FullName;
        Directory.CreateDirectory(Path.Combine(checkout, @"deps\qpdf\bin"));
        File.WriteAllText(Path.Combine(checkout, @"deps\qpdf\bin\qpdf.exe"), "binary");

        var run = Check(checkout);

        Assert.True(run.ExitCode == 0, run.Output); // a warning, not a failure: build and tests still run
        Assert.Contains(Users.Value, run.Output, StringComparison.Ordinal);
        Assert.Equal(FileSystemRights.Modify, InheritedRightsOf(checkout, Users)); // the check changed nothing
        AssertFixedByPrintedCommands(run.Output, checkout);
    }

    [Fact]
    public void A_checkout_with_an_entry_of_its_own_for_another_account_is_fixed_by_the_printed_command()
    {
        var checkout = Directory.CreateDirectory(Path.Combine(_parent, "checkout")).FullName;
        AddRule(checkout, Users, FileSystemRights.Modify, PropagationFlags.None);

        var run = Check(checkout);

        Assert.Contains($"/remove:g *{Users.Value}", run.Output, StringComparison.Ordinal);
        AssertFixedByPrintedCommands(run.Output, checkout);
    }

    [Fact]
    public void A_file_in_deps_with_entries_of_its_own_is_reported_and_fixed()
    {
        // Fixing the checkout's own entries leaves such a file as it is: it does not inherit.
        var checkout = Directory.CreateDirectory(Path.Combine(_parent, "checkout")).FullName;
        Directory.CreateDirectory(Path.Combine(checkout, @"deps\qpdf\bin"));
        var qpdf = Path.Combine(checkout, @"deps\qpdf\bin\qpdf.exe");
        File.WriteAllText(qpdf, "binary");
        var acl = new FileInfo(qpdf).GetAccessControl();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        acl.AddAccessRule(new FileSystemAccessRule(Interactive, FileSystemRights.Modify, AccessControlType.Allow));
        new FileInfo(qpdf).SetAccessControl(acl);

        var run = Check(checkout);

        Assert.Contains(Interactive.Value, run.Output, StringComparison.Ordinal);
        Assert.Contains(@"deps\qpdf\bin\qpdf.exe", run.Output, StringComparison.Ordinal);
        AssertFixedByPrintedCommands(run.Output, checkout);
    }

    [Fact]
    public void A_parent_other_accounts_can_rename_is_reported()
    {
        // Whoever can rename the folder that holds the checkout can put a checkout of their own in its place.
        AddRule(_parent, Users, FileSystemRights.Modify, PropagationFlags.None, inherit: false);
        var checkout = Directory.CreateDirectory(Path.Combine(_parent, "checkout")).FullName;
        SetPrivate(checkout);

        var run = Check(checkout);

        Assert.Contains(_parent, run.Output, StringComparison.Ordinal);
        Assert.Contains(Users.Value, run.Output, StringComparison.Ordinal);
        AssertFixedByPrintedCommands(run.Output, checkout);
    }

    [Fact]
    public void A_checkout_only_this_user_SYSTEM_and_administrators_can_change_gets_no_warning()
    {
        var checkout = Directory.CreateDirectory(Path.Combine(_parent, "checkout")).FullName;

        var run = Check(checkout);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.DoesNotContain("icacls", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_access_for_other_accounts_is_no_reason_to_warn()
    {
        var checkout = Directory.CreateDirectory(Path.Combine(_parent, "checkout")).FullName;
        AddRule(checkout, Users, FileSystemRights.ReadAndExecute, PropagationFlags.None);

        var run = Check(checkout);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.DoesNotContain("icacls", run.Output, StringComparison.Ordinal);
    }

    private (int ExitCode, string Output) Check(string checkout) =>
        PowerShellScript.Run("check-checkout.ps1", ["-Path", checkout, "-StopAt", _stopAt]);

    private void AssertFixedByPrintedCommands(string output, string checkout)
    {
        var commands = output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("icacls ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(commands);
        foreach (var command in commands)
        {
            using var icacls = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command}") { UseShellExecute = false, RedirectStandardOutput = true })!;
            icacls.StandardOutput.ReadToEnd();
            icacls.WaitForExit();
            Assert.True(icacls.ExitCode == 0, command);
        }

        var again = Check(checkout);
        Assert.True(!again.Output.Contains("icacls", StringComparison.Ordinal), again.Output);
    }

    private static void SetPrivate(string folder)
    {
        using var me = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { me.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(acl);
    }

    private static void AddRule(string folder, SecurityIdentifier sid, FileSystemRights rights, PropagationFlags propagation, bool inherit = true)
    {
        var info = new DirectoryInfo(folder);
        var acl = info.GetAccessControl();
        var inheritance = inherit ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        acl.AddAccessRule(new FileSystemAccessRule(sid, rights, inheritance, propagation, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }

    private static FileSystemRights InheritedRightsOf(string folder, SecurityIdentifier sid) =>
        new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.IdentityReference.Equals(sid) && rule.IsInherited)
            .Aggregate((FileSystemRights)0, (all, rule) => all | rule.FileSystemRights) & ~FileSystemRights.Synchronize;
}
