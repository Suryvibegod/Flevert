using ConvertAnything.Core.Menu;
using ConvertAnything.Core.Operations;

namespace ConvertAnything.Core.Tests;

public sealed class MenuTests
{
    private const string Exe = @"C:\Program Files\ConvertAnything\ca.exe";
    private readonly FakeRegistry _registry = new();

    [Fact]
    public void Adds_top_level_entry_for_each_supported_extension()
    {
        Install(new FakeOp("image.to-png", "Convert/PNG", ".jpg", ".heic"));

        foreach (var ext in new[] { ".jpg", ".heic" })
        {
            var key = $@"SystemFileAssociations\{ext}\shell\ConvertAnything";
            Assert.Equal("ConvertAnything", _registry.Value(key, "MUIVerb"));
            Assert.Equal($"\"{Exe}\",0", _registry.Value(key, "Icon"));
            Assert.Equal($"ConvertAnything.Menu{ext}", _registry.Value(key, "ExtendedSubCommandsKey"));
        }
    }

    [Fact]
    public void Writes_leaf_command_that_quotes_exe_and_file_and_allows_100_files()
    {
        Install(new DevCopy());

        var leaf = _registry.KeysUnder(@"ConvertAnything.Menu.txt\Dev\shell").Single();
        Assert.Equal("Copy", _registry.Value(leaf, "MUIVerb"));
        Assert.Equal("Player", _registry.Value(leaf, "MultiSelectModel"));
        Assert.Equal($"\"{Exe}\" run dev.copy \"%1\"", _registry.Value($@"{leaf}\command", ""));
    }

    [Fact]
    public void Operations_sharing_a_submenu_appear_under_one_entry_in_declared_order()
    {
        Install(
            new FakeOp("image.to-png", "Convert/PNG", ".jpg"),
            new FakeOp("image.to-webp", "Convert/WEBP", ".jpg"),
            new FakeOp("image.strip", "Remove metadata", ".jpg"));

        var top = _registry.KeysUnder(@"ConvertAnything.Menu.jpg\shell");
        Assert.Equal(["Convert", "Remove metadata"], top.Select(k => _registry.Value(k, "MUIVerb")));
        Assert.Equal(@"ConvertAnything.Menu.jpg\Convert", _registry.Value(top[0], "ExtendedSubCommandsKey"));

        var convert = _registry.KeysUnder(@"ConvertAnything.Menu.jpg\Convert\shell");
        Assert.Equal(["PNG", "WEBP"], convert.Select(k => _registry.Value(k, "MUIVerb")));
    }

    [Fact]
    public void Operation_does_not_appear_for_extensions_it_does_not_support()
    {
        Install(new FakeOp("image.to-png", "Convert/PNG", ".jpg"), new DevCopy());

        Assert.Empty(_registry.KeysUnder(@"ConvertAnything.Menu.txt\Convert\shell"));
        Assert.Equal(["Convert"], _registry.KeysUnder(@"ConvertAnything.Menu.jpg\shell").Select(k => _registry.Value(k, "MUIVerb")));
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

        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(child));
    }

    [Fact]
    public void Install_location_with_a_file_other_users_can_write_is_unsafe()
    {
        using var dir = new TempDir();
        ProtectToOwnerAndAdmins(dir.Path);
        var dll = dir.File("ca.dll");
        var acl = new FileInfo(dll).GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            System.Security.AccessControl.FileSystemRights.Write, System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(dll).SetAccessControl(acl);

        Assert.False(InstallLocation.IsWritableOnlyByOwnerAndAdmins(dir.Path));
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
        // Built explicitly: inherited ACLs vary by machine (e.g. sandbox tools add groups to the profile's Temp folder).
        using var dir = new TempDir();
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        foreach (var sid in new[]
                 {
                     me.User!,
                     new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                     new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid,
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(dir.Path).SetAccessControl(acl);

        Assert.True(InstallLocation.IsWritableOnlyByOwnerAndAdmins(dir.Path));
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

    private void Install(params IOperation[] ops) => new MenuInstaller(_registry, Exe).Install(ops);

    private sealed class FakeOp(string id, string menuPath, params string[] extensions) : IOperation
    {
        public string Id => id;
        public string MenuPath => menuPath;
        public IReadOnlyCollection<string> Extensions => extensions;
        public Task<string> RunAsync(string inputPath, CancellationToken ct) => throw new NotSupportedException();
    }
}
