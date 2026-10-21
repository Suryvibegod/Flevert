using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Flevert.Core.Menu;

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistry(string root = @"Software\Classes") : IRegistry
{
    public void SetValue(string key, string name, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Full(key));
        k.SetValue(name, value, RegistryValueKind.String);
    }

    public void SetDword(string key, string name, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Full(key));
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    public string? GetValue(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Full(key));
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();
    }

    public void DeleteTree(string key)
    {
        Registry.CurrentUser.DeleteSubKeyTree(Full(key), throwOnMissingSubKey: false);
        PruneEmptyAncestors(key);
    }

    public IReadOnlyList<string> SubKeyNames(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Full(key));
        return k?.GetSubKeyNames() ?? [];
    }

    // Stops below the first segment so shared parents such as SystemFileAssociations are never removed.
    private void PruneEmptyAncestors(string key)
    {
        for (var parent = Parent(key); parent.Contains('\\', StringComparison.Ordinal); parent = Parent(parent))
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Full(parent)))
            {
                if (k is null)
                    continue;
                if (k.SubKeyCount > 0 || k.ValueCount > 0)
                    return;
            }

            Registry.CurrentUser.DeleteSubKey(Full(parent), throwOnMissingSubKey: false);
        }
    }

    private static string Parent(string key) => key[..Math.Max(0, key.LastIndexOf('\\'))];

    private string Full(string key) => key.Length == 0 ? root : $@"{root}\{key}";
}
