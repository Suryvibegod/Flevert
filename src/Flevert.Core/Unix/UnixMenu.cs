using System.Runtime.Versioning;
using System.Text;

namespace Flevert.Core;

/// <summary>
/// Puts Flevert in the Nautilus menu for this user, and takes it out: menu.json in Flevert's own folder, and the
/// extension where nautilus-python loads extensions from (docs/specs/linux.md). Each file is written beside its place and
/// renamed over it, so a link put in its place is replaced, never written through.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class UnixMenuInstaller(string dataDirectory, string extensionsDirectory)
{
    public const string MenuFileName = "menu.json";
    public const string ExtensionFileName = "flevert_menu.py";

    /// <summary>~/.local/share/Flevert and ~/.local/share/nautilus-python/extensions (both under XDG_DATA_HOME when set).</summary>
    public static UnixMenuInstaller ForThisUser() =>
        new(AppData.Root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nautilus-python", "extensions"));

    public void Install(string menuJson, string extensionSource)
    {
        ArgumentNullException.ThrowIfNull(menuJson);
        SecureDirectory.TrySecure(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        Replace(Path.Combine(dataDirectory, MenuFileName), Encoding.UTF8.GetBytes(menuJson), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Directory.CreateDirectory(extensionsDirectory);
        Replace(Path.Combine(extensionsDirectory, ExtensionFileName), File.ReadAllBytes(extensionSource),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    /// <summary>Removes both files; a link at either name is removed itself.</summary>
    public void Uninstall()
    {
        File.Delete(Path.Combine(extensionsDirectory, ExtensionFileName));
        File.Delete(Path.Combine(dataDirectory, MenuFileName));
    }

    private static void Replace(string path, byte[] bytes, UnixFileMode mode)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = mode }))
                stream.Write(bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

/// <summary>
/// Whether nobody but this user (and root) can change the folder flevert runs from: whoever can would put another
/// flevert in its place, and the menu would run it as the user. The counterpart of InstallLocation on Windows.
/// </summary>
[SupportedOSPlatform("linux")]
public static class UnixInstallLocation
{
    private const int OthersMayWrite = 0x12; // S_IWGRP | S_IWOTH
    private const int Sticky = 0x200; // S_ISVTX: only an entry's owner may rename or delete it

    /// <summary>
    /// The folder and every folder above it are real folders (no link on the way), owned by this user or root, and
    /// nobody else can write them; a folder above that others can write is fine only with the sticky bit, which keeps
    /// them from renaming what is not theirs.
    /// </summary>
    public static bool IsWritableOnlyByOwner(string directory)
    {
        var me = Libc.EffectiveUserId;
        var path = Path.GetFullPath(directory);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Libc.TryStatNoFollow(current, out var stat) || stat.Type != Libc.SIfDir)
                return false;
            if (stat.Uid != me && stat.Uid != 0)
                return false;
            var othersMayWrite = (stat.Mode & OthersMayWrite) != 0;
            if (othersMayWrite && (current == path || (stat.Mode & Sticky) == 0))
                return false;
        }

        return true;
    }
}
