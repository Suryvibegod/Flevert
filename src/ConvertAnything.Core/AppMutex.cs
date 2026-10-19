namespace ConvertAnything.Core;

/// <summary>
/// Held by every running ca.exe. The installer's AppMutex setting waits on it, so an upgrade or uninstall does not run
/// while ca.exe has its folder open: files in use cannot be removed by a per-user uninstaller, and they would stay
/// behind with no uninstaller left to remove them.
/// </summary>
public static class AppMutex
{
    public const string Name = "ConvertAnything.App";
}
