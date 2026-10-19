namespace ConvertAnything.Core;

/// <summary>
/// Held by every running ca.exe. With the installer's AppMutex setting, setup and uninstall ask the user to wait for it
/// (or cancel), so an upgrade or uninstall does not run while ca.exe has its folder open: files in use cannot be removed
/// by a per-user uninstaller, and they would stay behind with no uninstaller left to remove them.
/// </summary>
public static class AppMutex
{
    public const string Name = "ConvertAnything.App";

    /// <summary>
    /// Opens or creates the mutex; it marks the process as running just by existing, so nothing waits on it. Null when
    /// another process in the session (elevated, or another account) created the name without access for us: the mutex
    /// is advisory, and refusing to run would let anyone break every click on the menu.
    /// </summary>
    public static Mutex? TryHold()
    {
        try
        {
            return new Mutex(initiallyOwned: false, Name);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            return null;
        }
    }
}
