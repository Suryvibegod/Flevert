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
