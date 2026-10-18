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

    private void Install(params IOperation[] ops) => new MenuInstaller(_registry, Exe).Install(ops);

    private sealed class FakeOp(string id, string menuPath, params string[] extensions) : IOperation
    {
        public string Id => id;
        public string MenuPath => menuPath;
        public IReadOnlyCollection<string> Extensions => extensions;
        public Task<string> RunAsync(string inputPath, CancellationToken ct) => throw new NotSupportedException();
    }
}
