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

    /// <summary>Relative to HKCU\Software\Classes. The installer's uninstall safety net repeats it.</summary>
    internal const string Key = $@"{ClsidRoot}\{Clsid}";

    private const string ClsidRoot = "CLSID";
    private const string Clsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string Server = $@"{Key}\InprocServer32";

    /// <summary>
    /// Creates and marks the key unless it exists. False when it exists but would not switch the menu (no InprocServer32
    /// or a non-empty default): someone else's key, left unchanged, so the caller can say the menu will not change.
    /// </summary>
    public bool On()
    {
        if (!Exists())
        {
            registry.SetValue(Key, Marker, "1");
            registry.SetValue(Server, "", "");
            return true;
        }

        return registry.GetValue(Server, "") is "";
    }

    public void Off()
    {
        if (registry.GetValue(Key, Marker) is not null)
            registry.DeleteTree(Key);
    }

    private bool Exists() => registry.SubKeyNames(ClsidRoot).Contains(Clsid, StringComparer.OrdinalIgnoreCase);
}
