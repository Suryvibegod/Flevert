namespace Flevert.Core.Tests;

/// <summary>
/// On Linux the folders Flevert keeps its own files in (logs, ImageMagick's configuration and pixel caches) are open to
/// this user only: mode 0700, the counterpart of the Windows ACL for the user, SYSTEM and Administrators.
/// </summary>
public sealed class UnixSecureDirectoryTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_new_folder_is_created_for_this_user_only()
    {
        var folder = _dir.Combine("logs");

        SecureDirectory.TrySecure(folder);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(folder));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void An_existing_folder_others_can_read_is_closed_to_them()
    {
        var folder = Directory.CreateDirectory(_dir.Combine("logs")).FullName;
        File.SetUnixFileMode(folder, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute | UnixFileMode.GroupWrite);

        SecureDirectory.RestrictToOwner(folder);

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(folder));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void A_symbolic_link_in_place_of_the_folder_is_refused_and_its_target_left_alone()
    {
        var target = Directory.CreateDirectory(_dir.Combine("elsewhere")).FullName;
        const UnixFileMode open = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(target, open);
        var link = _dir.Combine("logs");
        Directory.CreateSymbolicLink(link, target);

        Assert.ThrowsAny<IOException>(() => SecureDirectory.RestrictToOwner(link));

        Assert.Equal(open, File.GetUnixFileMode(target));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Securing_never_throws_even_when_the_folder_cannot_be_made()
    {
        var blocker = _dir.File("logs"); // a file where the folder should be

        SecureDirectory.TrySecure(Path.Combine(blocker, "inside"));
    }
}
