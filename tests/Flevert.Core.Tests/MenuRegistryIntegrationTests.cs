using Flevert.Core.Menu;
using Flevert.Core.Operations;
using Microsoft.Win32;

namespace Flevert.Core.Tests;

/// <summary>Writes to the real registry, but only below HKCU\Software\Classes\Flevert.Test.</summary>
public sealed class MenuRegistryIntegrationTests : IDisposable
{
    private const string TestRoot = @"Software\Classes\Flevert.Test";
    private readonly WindowsRegistry _registry = new(TestRoot);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);

    [Fact]
    public void Install_writes_real_keys_and_uninstall_removes_them_all()
    {
        var installer = new MenuInstaller(_registry, @"C:\flevert.exe");

        installer.Install([new DevCopy()]);

        using (var top = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\SystemFileAssociations\.txt\shell\Flevert"))
        {
            Assert.NotNull(top);
            Assert.Equal("Flevert.Menu.txt", top.GetValue("ExtendedSubCommandsKey"));
        }

        installer.Uninstall();

        Assert.DoesNotContain(AllKeyPaths(TestRoot), k => k.Contains("Flevert", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_separator_is_written_as_the_dword_explorer_reads()
    {
        var looks = new Dictionary<string, MenuEntryLook> { ["Dev"] = new("toolbox", 1), ["Other"] = new("toolbox", 2) };

        new MenuInstaller(_registry, @"C:\flevert.exe").Install([new DevCopy(), new OtherOp()], looks);

        using var second = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\Flevert.Menu.txt\shell\001");
        Assert.NotNull(second);
        Assert.Equal(RegistryValueKind.DWord, second.GetValueKind("CommandFlags"));
        Assert.Equal(0x20, second.GetValue("CommandFlags"));
    }

    [Fact]
    public void Install_and_uninstall_of_both_menus_leave_nothing_behind()
    {
        var installer = new MenuInstaller(_registry, @"C:\flevert.exe");

        installer.Install([new DevCopy(), new ToolOp()]);
        using (var tools = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\SystemFileAssociations\.txt\shell\Flevert.Tools"))
        {
            Assert.NotNull(tools);
            Assert.Equal("Flevert.Menu.txt.tools", tools.GetValue("ExtendedSubCommandsKey"));
        }

        installer.Uninstall();

        // SystemFileAssociations itself is shared with other apps and stays.
        Assert.DoesNotContain(AllKeyPaths(TestRoot), k => k.Contains("Flevert", StringComparison.OrdinalIgnoreCase) || k.Contains(".txt", StringComparison.Ordinal));
    }

    private sealed class ToolOp : IOperation, IMenuPlacement
    {
        public string Id => "dev.tool";
        public string MenuPath => "Tool";
        public MenuCascade Cascade => MenuCascade.PdfTools;
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public Task<string> RunAsync(string inputPath, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class OtherOp : IOperation
    {
        public string Id => "dev.other";
        public string MenuPath => "Other";
        public IReadOnlyCollection<string> Extensions { get; } = [".txt"];
        public Task<string> RunAsync(string inputPath, CancellationToken ct) => throw new NotSupportedException();
    }

    private static IEnumerable<string> AllKeyPaths(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        foreach (var name in key?.GetSubKeyNames() ?? [])
        {
            yield return name;
            foreach (var child in AllKeyPaths($@"{path}\{name}"))
                yield return $@"{name}\{child}";
        }
    }

    [Fact]
    public void Uninstall_on_clean_registry_is_a_no_op()
    {
        new MenuInstaller(_registry, @"C:\flevert.exe").Uninstall();
    }
}
