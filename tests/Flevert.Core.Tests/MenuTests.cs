using Flevert.Core.Menu;
using Flevert.Core.Operations;

namespace Flevert.Core.Tests;

public sealed class MenuTests
{
    private const string Exe = @"C:\Program Files\Flevert\flevert.exe";
    private readonly FakeRegistry _registry = new();

    [Fact]
    public void Adds_top_level_entry_for_each_supported_extension()
    {
        Install(new FakeOp("image.to-png", "Convert/PNG", ".jpg", ".heic"));

        foreach (var ext in new[] { ".jpg", ".heic" })
        {
            var key = $@"SystemFileAssociations\{ext}\shell\Flevert";
            Assert.Equal("Flevert", _registry.GetValue(key, "MUIVerb"));
            Assert.Equal($"\"{Exe}\",0", _registry.GetValue(key, "Icon"));
            Assert.Equal($"Flevert.Menu{ext}", _registry.GetValue(key, "ExtendedSubCommandsKey"));
        }
    }

    [Fact]
    public void Writes_leaf_command_that_quotes_exe_and_file_and_allows_100_files()
    {
        Install(new DevCopy());

        var leaf = _registry.KeysUnder(@"Flevert.Menu.txt\Dev\shell").Single();
        Assert.Equal("Copy", _registry.GetValue(leaf, "MUIVerb"));
        Assert.Equal("Player", _registry.GetValue(leaf, "MultiSelectModel"));
        Assert.Equal($"\"{Exe}\" run dev.copy \"%1\"", _registry.GetValue($@"{leaf}\command", ""));
    }

    [Fact]
    public void An_operation_that_joins_the_batch_gets_the_same_one_file_command_beside_the_others()
    {
        // Explorer still starts one process per selected file; the batch leader collects them (ADR-0007, part 2).
        Install(new DevCopy(), new DevJoin());

        var leaves = _registry.KeysUnder(@"Flevert.Menu.txt\Dev\shell");
        Assert.Equal(["Copy", "Join"], leaves.Select(k => _registry.GetValue(k, "MUIVerb")));
        Assert.Equal("Player", _registry.GetValue(leaves[1], "MultiSelectModel"));
        Assert.Equal($"\"{Exe}\" run dev.join \"%1\"", _registry.GetValue($@"{leaves[1]}\command", ""));
    }

    [Fact]
    public void Operations_sharing_a_submenu_appear_under_one_entry_in_declared_order()
    {
        Install(
            new FakeOp("image.to-png", "Convert/PNG", ".jpg"),
            new FakeOp("image.to-webp", "Convert/WEBP", ".jpg"),
            new FakeOp("image.strip", "Remove metadata", ".jpg"));

        var top = _registry.KeysUnder(@"Flevert.Menu.jpg\shell");
        Assert.Equal(["Convert", "Remove metadata"], top.Select(k => _registry.GetValue(k, "MUIVerb")));
        Assert.Equal(@"Flevert.Menu.jpg\Convert", _registry.GetValue(top[0], "ExtendedSubCommandsKey"));

        var convert = _registry.KeysUnder(@"Flevert.Menu.jpg\Convert\shell");
        Assert.Equal(["PNG", "WEBP"], convert.Select(k => _registry.GetValue(k, "MUIVerb")));
    }

    [Fact]
    public void Entries_at_the_top_get_their_icon_from_the_icons_folder_beside_flevert_and_formats_get_none()
    {
        var looks = new Dictionary<string, MenuEntryLook> { ["Convert to"] = new("arrow_swap", 1), ["Remove hidden info"] = new("shield", 1) };
        new MenuInstaller(_registry, Exe, iconExists: _ => true).Install(
            [new FakeOp("image.to-png", "Convert to/PNG", ".jpg"), new FakeOp("image.strip", "Remove hidden info", ".jpg")], looks);

        var top = _registry.KeysUnder(@"Flevert.Menu.jpg\shell");
        Assert.Equal(@"""C:\Program Files\Flevert\icons\arrow_swap.ico"",0", _registry.GetValue(top[0], "Icon"));
        Assert.Equal(@"""C:\Program Files\Flevert\icons\shield.ico"",0", _registry.GetValue(top[1], "Icon"));
        Assert.Null(_registry.GetValue(_registry.KeysUnder(@"Flevert.Menu.jpg\Convert to\shell").Single(), "Icon"));
        // The Flevert entry itself keeps the icon of flevert.exe.
        Assert.Equal($"\"{Exe}\",0", _registry.GetValue(@"SystemFileAssociations\.jpg\shell\Flevert", "Icon"));
    }

    [Fact]
    public void An_icon_file_that_is_missing_leaves_its_entry_without_an_icon()
    {
        // A development build has no icons folder: the menu is still written, without icons.
        var looks = new Dictionary<string, MenuEntryLook> { ["Convert to"] = new("arrow_swap", 1), ["Remove hidden info"] = new("shield", 1) };
        new MenuInstaller(_registry, Exe, iconExists: path => path.EndsWith("shield.ico", StringComparison.Ordinal)).Install(
            [new FakeOp("image.to-png", "Convert to/PNG", ".jpg"), new FakeOp("image.strip", "Remove hidden info", ".jpg")], looks);

        var top = _registry.KeysUnder(@"Flevert.Menu.jpg\shell");
        Assert.Null(_registry.GetValue(top[0], "Icon"));
        Assert.NotNull(_registry.GetValue(top[1], "Icon"));
    }

    [Fact]
    public void A_separator_goes_before_the_first_entry_of_each_new_group_only()
    {
        var looks = new Dictionary<string, MenuEntryLook>
        {
            ["Compress"] = new("arrow_minimize", 1),
            ["Merge PDFs"] = new("merge", 1),
            ["Protect with password"] = new("lock_closed", 2),
            ["Remove password"] = new("lock_open", 2),
            ["Remove hidden info"] = new("shield", 3),
        };
        new MenuInstaller(_registry, Exe, iconExists: _ => true).Install(
            [
                new FakeOp("pdf.compress", "Compress/Smaller, same quality", ".pdf"),
                new FakeOp("pdf.merge", "Merge PDFs", ".pdf"),
                new FakeOp("pdf.protect", "Protect with password", ".pdf"),
                new FakeOp("pdf.unlock", "Remove password", ".pdf"),
                new FakeOp("pdf.strip-metadata", "Remove hidden info", ".pdf"),
            ], looks);

        // ECF_SEPARATORBEFORE. Separators do not count toward the 16 entries Explorer shows (measured with MenuProbe).
        Assert.Equal(
            [null, null, "32", null, "32"],
            _registry.KeysUnder(@"Flevert.Menu.pdf\shell").Select(verb => _registry.GetValue(verb, "CommandFlags")));
    }

    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("Arrow Swap")]
    [InlineData("")]
    public void Rejects_icon_names_that_are_not_plain_fluent_names(string icon)
    {
        var looks = new Dictionary<string, MenuEntryLook> { ["Convert"] = new(icon, 1) };

        Assert.Throws<ArgumentException>(() => new MenuInstaller(_registry, Exe).Install([new FakeOp("image.to-png", "Convert/PNG", ".jpg")], looks));
    }

    [Fact]
    public void An_operation_placed_in_the_pdf_tools_menu_gets_a_second_top_level_entry_with_its_own_entries()
    {
        new MenuInstaller(_registry, Exe, iconExists: _ => true).Install(
            [new FakeOp("pdf.merge", "Merge PDFs", ".pdf"), new PlacedOp("pdf.flatten", "Lock filled forms", ".pdf")]);

        var tools = @"SystemFileAssociations\.pdf\shell\Flevert.Tools";
        Assert.Equal("Flevert PDF tools", _registry.GetValue(tools, "MUIVerb"));
        Assert.Equal("Flevert.Menu.pdf.tools", _registry.GetValue(tools, "ExtendedSubCommandsKey"));
        Assert.Equal("Player", _registry.GetValue(tools, "MultiSelectModel"));
        Assert.Equal(@"""C:\Program Files\Flevert\icons\toolbox.ico"",0", _registry.GetValue(tools, "Icon"));
        Assert.Equal(["Lock filled forms"], _registry.KeysUnder(@"Flevert.Menu.pdf.tools\shell").Select(k => _registry.GetValue(k, "MUIVerb")));
        Assert.Equal(["Merge PDFs"], _registry.KeysUnder(@"Flevert.Menu.pdf\shell").Select(k => _registry.GetValue(k, "MUIVerb")));
        Assert.Equal($"\"{Exe}\" run pdf.flatten \"%1\"", _registry.GetValue($@"{_registry.KeysUnder(@"Flevert.Menu.pdf.tools\shell")[0]}\command", ""));
        // A type with no tool gets no second entry.
        Assert.Null(_registry.GetValue(@"SystemFileAssociations\.jpg\shell\Flevert.Tools", "MUIVerb"));
    }

    [Fact]
    public void The_second_menu_shows_flevert_s_own_icon_when_its_icon_is_missing()
    {
        Install(new PlacedOp("pdf.flatten", "Lock filled forms", ".pdf"));

        Assert.Equal($"\"{Exe}\",0", _registry.GetValue(@"SystemFileAssociations\.pdf\shell\Flevert.Tools", "Icon"));
    }

    [Fact]
    public void Uninstall_removes_both_menus_and_nothing_else()
    {
        _registry.SetValue(@"SystemFileAssociations\.pdf\shell\SomeoneElse", "MUIVerb", "Keep me");
        Install(new FakeOp("pdf.merge", "Merge PDFs", ".pdf"), new PlacedOp("pdf.flatten", "Lock filled forms", ".pdf"));

        new MenuInstaller(_registry, Exe).Uninstall();

        Assert.Equal([@"SystemFileAssociations\.pdf\shell\SomeoneElse"], _registry.Snapshot().Keys);
    }

    [Fact]
    public void An_operation_placed_in_a_menu_flevert_does_not_know_is_refused()
    {
        // Uninstall removes only the menus it knows (MenuCascade.All); another would be left behind.
        var stray = new PlacedOp("pdf.flatten", "Flatten", ".pdf", new MenuCascade("Flevert.Other", "Other", ".other", null));

        Assert.Throws<ArgumentException>(() => Install(stray));
    }

    [Fact]
    public void Operation_does_not_appear_for_extensions_it_does_not_support()
    {
        Install(new FakeOp("image.to-png", "Convert/PNG", ".jpg"), new DevCopy());

        Assert.Empty(_registry.KeysUnder(@"Flevert.Menu.txt\Convert\shell"));
        Assert.Equal(["Convert"], _registry.KeysUnder(@"Flevert.Menu.jpg\shell").Select(k => _registry.GetValue(k, "MUIVerb")));
    }

    [Fact]
    public void Installing_twice_gives_the_same_registry_state()
    {
        Install(new DevCopy());
        var first = _registry.Snapshot();

        Install(new DevCopy());

        Assert.Equal(first, _registry.Snapshot());
    }

    [Fact]
    public void Reinstalling_with_fewer_operations_removes_the_old_entries()
    {
        Install(new FakeOp("image.to-png", "Convert/PNG", ".jpg"), new DevCopy());

        Install(new DevCopy());

        Assert.DoesNotContain(_registry.Snapshot().Keys, k => k.Contains(".jpg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Uninstall_removes_every_key_it_created_and_nothing_else()
    {
        _registry.SetValue(@"SystemFileAssociations\.txt\shell\SomeoneElse", "MUIVerb", "Keep me");
        Install(new DevCopy(), new FakeOp("image.to-png", "Convert/PNG", ".jpg"));

        new MenuInstaller(_registry, Exe).Uninstall();

        Assert.Equal(["SystemFileAssociations\\.txt\\shell\\SomeoneElse"], _registry.Snapshot().Keys);
    }

    [Theory]
    [InlineData("image to-png", "Convert/PNG")]   // space would split the registry command line
    [InlineData("Image.ToPng", "Convert/PNG")]    // ids are lower-case
    [InlineData("image.to-png", "Convert\\PNG")]  // backslash would create a registry subkey
    [InlineData("image.to-png", "shell/PNG")]     // collides with the reserved "shell" subkey
    [InlineData("image.to-png", "Convert//PNG")]  // empty label
    public void Rejects_operations_that_would_corrupt_the_registry_layout(string id, string menuPath)
    {
        Assert.Throws<ArgumentException>(() => Install(new FakeOp(id, menuPath, ".jpg")));
    }

    [Fact]
    public void An_operation_without_a_menu_path_writes_nothing_to_the_menu()
    {
        // A PDF tool as PdfTools makes it, before the menu places it (docs/specs/pdf.md, v0.2.2): no key, no command.
        Install(new FakeOp("pdf.flatten", "", ".pdf"), new FakeOp("pdf.merge", "Merge PDFs", ".pdf"), new FakeOp("image.to-png", "Convert/PNG", ".jpg"));

        Assert.Equal(["Merge PDFs"], _registry.KeysUnder(@"Flevert.Menu.pdf\shell").Select(k => _registry.GetValue(k, "MUIVerb")));
        Assert.DoesNotContain(_registry.Snapshot().Values, v => v.Contains("pdf.flatten", StringComparison.Ordinal));
        Assert.DoesNotContain(_registry.Snapshot().Keys, key => key.Contains("flatten", StringComparison.OrdinalIgnoreCase));
        Assert.Single(_registry.KeysUnder(@"Flevert.Menu.jpg\shell"));
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData(@".j\pg")]
    [InlineData(".JPG")]
    [InlineData(".")]
    public void Rejects_extensions_that_are_not_lower_case_dot_tokens(string ext)
    {
        Assert.Throws<ArgumentException>(() => Install(new FakeOp("image.to-png", "Convert/PNG", ext)));
    }

    [Fact]
    public void Install_location_whose_parent_lets_other_users_delete_children_is_unsafe()
    {
        using var parent = new TempDir();
        var child = Directory.CreateDirectory(Path.Combine(parent.Path, "app")).FullName;
        ProtectToOwnerAndAdmins(child);
        var acl = new DirectoryInfo(parent.Path).GetAccessControl();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in OwnerAndAdmins())
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles,
            System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(parent.Path).SetAccessControl(acl);

        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(child, stopAt: Path.GetDirectoryName(parent.Path)));
    }

    [Fact]
    public void Install_location_with_a_file_other_users_can_write_is_unsafe()
    {
        using var dir = new TempDir();
        ProtectToOwnerAndAdmins(dir.Path);
        var dll = dir.File("flevert.dll");
        var acl = new FileInfo(dll).GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            System.Security.AccessControl.FileSystemRights.Write, System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(dll).SetAccessControl(acl);

        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(dir.Path));
    }

    // Setup checks the install folder while it is still empty. An entry that applies only to files created later
    // (inherit-only) would give every copied flevert.exe and DLL to other accounts, so it counts although it does not
    // apply to the folder itself.
    [Theory]
    [InlineData(System.Security.AccessControl.InheritanceFlags.ObjectInherit)]
    [InlineData(System.Security.AccessControl.InheritanceFlags.ContainerInherit)]
    public void Install_location_that_would_hand_new_files_to_other_users_is_unsafe(System.Security.AccessControl.InheritanceFlags inheritance)
    {
        using var parent = new TempDir();
        ProtectToOwnerAndAdmins(parent.Path);
        var app = Directory.CreateDirectory(Path.Combine(parent.Path, "app")).FullName;
        Assert.True(InstallLocation.IsWritableOnlyByOwnerAndAdmins(app, stopAt: Path.GetDirectoryName(parent.Path)), "control");

        var acl = new DirectoryInfo(app).GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
            System.Security.AccessControl.FileSystemRights.Modify, inheritance,
            System.Security.AccessControl.PropagationFlags.InheritOnly, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(app).SetAccessControl(acl);

        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(app, stopAt: Path.GetDirectoryName(parent.Path)));
    }

    [Theory]
    [InlineData(System.Security.Principal.WellKnownSidType.CreatorOwnerSid, true)] // becomes the user who creates each file
    [InlineData(System.Security.Principal.WellKnownSidType.CreatorGroupSid, false)] // becomes their primary group, shared with others
    public void Inherit_only_creator_entries_are_judged_by_who_they_become(System.Security.Principal.WellKnownSidType creator, bool safe)
    {
        using var parent = new TempDir();
        ProtectToOwnerAndAdmins(parent.Path);
        var app = Directory.CreateDirectory(Path.Combine(parent.Path, "app")).FullName;
        var acl = new DirectoryInfo(app).GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(creator, null),
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.InheritOnly, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(app).SetAccessControl(acl);

        Assert.Equal(safe, InstallLocation.IsWritableOnlyByOwnerAndAdmins(app, stopAt: Path.GetDirectoryName(parent.Path)));
    }

    [Fact]
    public void Write_that_a_parent_passes_to_new_files_makes_the_install_location_unsafe()
    {
        // Write without Delete on the parent is harmless to the parent itself, but the install folder inherits it for
        // the files setup will copy.
        using var parent = new TempDir();
        ProtectToOwnerAndAdmins(parent.Path);
        var acl = new DirectoryInfo(parent.Path).GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
            System.Security.AccessControl.FileSystemRights.Write, System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(parent.Path).SetAccessControl(acl);
        var app = Directory.CreateDirectory(Path.Combine(parent.Path, "app")).FullName;

        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(app, stopAt: Path.GetDirectoryName(parent.Path)));
    }

    [Fact]
    public void Inherit_only_entries_on_a_parent_do_not_make_the_install_location_unsafe()
    {
        // C:\ grants Authenticated Users inherit-only Modify for new folders; the install folder's own ACL shows what
        // actually reached it, so a parent's inherit-only entries are not held against it.
        using var parent = new TempDir();
        ProtectToOwnerAndAdmins(parent.Path);
        var acl = new DirectoryInfo(parent.Path).GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            System.Security.AccessControl.FileSystemRights.Modify, System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.InheritOnly | System.Security.AccessControl.PropagationFlags.NoPropagateInherit,
            System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(parent.Path).SetAccessControl(acl);
        var app = Path.Combine(parent.Path, "app");
        Directory.CreateDirectory(app);
        ProtectToOwnerAndAdmins(app);

        Assert.True(InstallLocation.IsWritableOnlyByOwnerAndAdmins(app, stopAt: Path.GetDirectoryName(parent.Path)));
    }

    [Fact]
    public void Install_location_whose_parent_other_users_may_modify_is_unsafe()
    {
        // Modify includes DELETE on the parent itself: it can be renamed away and rebuilt with a planted flevert.exe.
        using var parent = new TempDir();
        var child = Directory.CreateDirectory(Path.Combine(parent.Path, "app")).FullName;
        ProtectToOwnerAndAdmins(child);
        var acl = new DirectoryInfo(parent.Path).GetAccessControl();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in OwnerAndAdmins())
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            System.Security.AccessControl.FileSystemRights.Modify, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(parent.Path).SetAccessControl(acl);

        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(child, stopAt: Path.GetDirectoryName(parent.Path)));
    }

    // The ACL check reads the link target; the link itself lives in its parent and inherits that folder's rights, so
    // another account that can delete it can rebuild the path with a planted flevert.exe.
    [Theory]
    [InlineData(@"link")]
    [InlineData(@"link\app")]
    public void Install_location_reached_through_a_junction_is_unsafe(string viaLink)
    {
        using var parent = new TempDir();
        ProtectToOwnerAndAdmins(parent.Path);
        var real = Directory.CreateDirectory(Path.Combine(parent.Path, "real", "app")).FullName;
        var target = viaLink == "link" ? real : Path.GetDirectoryName(real)!;
        Junction(Path.Combine(parent.Path, "link"), target);

        var installed = Path.Combine(parent.Path, viaLink);
        Assert.True(InstallLocation.IsWritableOnlyByOwnerAndAdmins(real, stopAt: Path.GetDirectoryName(parent.Path)), "control: the target itself is safe");
        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(installed, stopAt: Path.GetDirectoryName(parent.Path)));
    }

    [SkippableFact]
    public void Install_location_on_a_subst_drive_is_unsafe()
    {
        // The parent walk stops at the drive root, so the real parents of a subst target are never checked; subst
        // drives also vanish on reboot and the letter can later belong to a USB stick.
        using var dir = new TempDir();
        ProtectToOwnerAndAdmins(dir.Path);
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "ZYXWVUTSRQPONMLKJ".FirstOrDefault(c => !used.Contains(c));
        Skip.If(letter == default, "no free drive letter");
        // GetDrives misses letters mapped in another logon session; then subst fails and the test cannot run.
        Skip.If(Cmd($"subst {letter}: \"{dir.Path}\"") != 0, $"{letter}: is taken");
        try
        {
            Assert.True(InstallLocation.IsWritableOnlyByOwnerAndAdmins(dir.Path, stopAt: Path.GetDirectoryName(dir.Path)), "control: the folder itself is safe");
            Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins($@"{letter}:\"));
        }
        finally
        {
            Cmd($"subst {letter}: /d");
        }
    }

    [Fact]
    public void Install_location_containing_a_junction_is_unsafe()
    {
        // The ACL check of an entry follows its link, so a junction inside would be judged by its target, whose own
        // parents are never checked.
        using var parent = new TempDir();
        ProtectToOwnerAndAdmins(parent.Path);
        var app = Directory.CreateDirectory(Path.Combine(parent.Path, "app")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(parent.Path, "elsewhere")).FullName;
        Assert.True(InstallLocation.IsWritableOnlyByOwnerAndAdmins(app, stopAt: Path.GetDirectoryName(parent.Path)), "control");

        Junction(Path.Combine(app, "plugins"), elsewhere);

        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(app, stopAt: Path.GetDirectoryName(parent.Path)));
    }

    private static int Cmd(string arguments)
    {
        using var cmd = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        cmd.WaitForExit();
        return cmd.ExitCode;
    }

    private static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    private static System.Security.Principal.SecurityIdentifier[] OwnerAndAdmins()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        return
        [
            me.User!,
            new(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            new(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
        ];
    }

    private static void ProtectToOwnerAndAdmins(string path)
    {
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in OwnerAndAdmins())
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    [Fact]
    public void Rejects_labels_that_differ_only_in_case()
    {
        Assert.Throws<ArgumentException>(() => Install(
            new FakeOp("image.a", "Convert/PNG", ".jpg"),
            new FakeOp("image.b", "convert/JPG", ".jpg")));
    }

    [Fact]
    public void Rejects_a_label_that_is_both_a_command_and_a_submenu()
    {
        Assert.Throws<ArgumentException>(() => Install(
            new FakeOp("dev.a", "Dev", ".txt"),
            new FakeOp("dev.b", "Dev/Copy", ".txt")));
    }

    [Fact]
    public void Install_location_with_only_user_system_and_admin_write_access_is_safe()
    {
        // The whole checked chain is built here: inherited ACLs on real parents vary by machine (sandbox tools, for one,
        // add groups to the profile's Temp folder), so the walk stops at the folder this test does not control.
        using var parent = new TempDir();
        ProtectToOwnerAndAdmins(parent.Path);
        var child = Directory.CreateDirectory(Path.Combine(parent.Path, "app")).FullName;

        Assert.True(InstallLocation.IsWritableOnlyByOwnerAndAdmins(child, stopAt: Path.GetDirectoryName(parent.Path)));
    }

    [Fact]
    public void Install_location_that_other_users_can_write_is_unsafe()
    {
        using var dir = new TempDir();
        var acl = new DirectoryInfo(dir.Path).GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(dir.Path).SetAccessControl(acl);

        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(dir.Path));
    }

    private void Install(params IMenuOperation[] ops) => new MenuInstaller(_registry, Exe).Install(ops);

    private sealed class PlacedOp(string id, string menuPath, string extension, MenuCascade? cascade = null) : IOperation, IMenuPlacement
    {
        public string Id => id;
        public string MenuPath => menuPath;
        public MenuCascade Cascade => cascade ?? MenuCascade.PdfTools;
        public IReadOnlyCollection<string> Extensions => [extension];
        public Task<string> RunAsync(string inputPath, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeOp(string id, string menuPath, params string[] extensions) : IOperation
    {
        public string Id => id;
        public string MenuPath => menuPath;
        public IReadOnlyCollection<string> Extensions => extensions;
        public Task<string> RunAsync(string inputPath, CancellationToken ct) => throw new NotSupportedException();
    }
}
