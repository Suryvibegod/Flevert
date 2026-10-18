using ConvertAnything.Core.Menu;
using ConvertAnything.Core.Operations;
using Microsoft.Win32;

namespace ConvertAnything.Core.Tests;

/// <summary>Writes to the real registry, but only below HKCU\Software\Classes\ConvertAnything.Test.</summary>
public sealed class MenuRegistryIntegrationTests : IDisposable
{
    private const string TestRoot = @"Software\Classes\ConvertAnything.Test";
    private readonly WindowsRegistry _registry = new(TestRoot);

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);

    [Fact]
    public void Install_writes_real_keys_and_uninstall_removes_them_all()
    {
        var installer = new MenuInstaller(_registry, @"C:\ca.exe");

        installer.Install([new DevCopy()]);

        using (var top = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\SystemFileAssociations\.txt\shell\ConvertAnything"))
        {
            Assert.NotNull(top);
            Assert.Equal("ConvertAnything.Menu.txt", top.GetValue("ExtendedSubCommandsKey"));
        }

        installer.Uninstall();

        Assert.DoesNotContain(AllKeyPaths(TestRoot), k => k.Contains("ConvertAnything", StringComparison.OrdinalIgnoreCase));
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
        new MenuInstaller(_registry, @"C:\ca.exe").Uninstall();
    }
}
