namespace ConvertAnything.Core.Menu;

/// <summary>
/// Windows 11 shows the classic right-click menu, where ConvertAnything lives, when this per-user key exists with an empty
/// default value. It changes every app's menu, so only a key this class created is ever removed. Explorer reads it at
/// startup: it takes effect after Explorer restarts or the user signs in again.
/// </summary>
public sealed class ClassicMenu(IRegistry registry)
{
    /// <summary>Value set on the CLSID key when ConvertAnything created it.</summary>
    public const string Marker = "ConvertAnything";

    private const string ClsidRoot = "CLSID";
    private const string Clsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string Key = $@"{ClsidRoot}\{Clsid}";

    public void On()
    {
        if (Exists())
            return; // the user's own setting (or ours from before); either way, nothing to add
        registry.SetValue(Key, Marker, "1");
        registry.SetValue($@"{Key}\InprocServer32", "", "");
    }

    public void Off()
    {
        if (Exists() && registry.GetValue(Key, Marker) is not null)
            registry.DeleteTree(Key);
    }

    private bool Exists() => registry.SubKeyNames(ClsidRoot).Contains(Clsid, StringComparer.OrdinalIgnoreCase);
}
