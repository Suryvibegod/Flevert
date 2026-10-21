using System.Text;

namespace Flevert.Core.Tests;

/// <summary>
/// Outputs on Linux: names fit the 255 bytes a Linux name may take (not 255 UTF-16 characters, as on Windows), and the
/// folder of a one-to-many operation is written in a folder only this user can open and renamed without following a
/// link or replacing anything (docs/specs/linux.md).
/// </summary>
public sealed class UnixOutputPathTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [LinuxTheory]
    [SupportedOSPlatform("linux")]
    [InlineData("ả", " (pages)")] // 3 bytes each in UTF-8
    [InlineData("🍊", " 001.pdf")] // 4 bytes, two UTF-16 units
    [InlineData("a", " (1).pdf")]
    public void A_long_name_is_cut_to_255_bytes_between_characters(string letter, string tail)
    {
        var stem = string.Concat(Enumerable.Repeat(letter, 300));

        var name = OutputPath.FitName(stem, tail);

        Assert.EndsWith(tail, name, StringComparison.Ordinal);
        Assert.InRange(Encoding.UTF8.GetByteCount(name), 255 - 3, 255);
        Assert.Equal(name, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(name))); // no character cut in half
        Assert.StartsWith(letter, name, StringComparison.Ordinal);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_folder_with_a_long_vietnamese_name_still_gets_its_counter()
    {
        var name = string.Concat(Enumerable.Repeat("Ảnh chụp ", 40)).Trim() + " (pages)";
        var desired = _dir.Combine(OutputPath.FitName(name[..^" (pages)".Length], " (pages)"));
        Directory.CreateDirectory(desired);

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(desired, " (pages)", WritePages(1), CancellationToken.None);

        Assert.EndsWith(" (pages) (1)", final, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(Path.GetFileName(final)) <= 255);
        Assert.Equal(["1.txt"], EntryNames(final));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_writer_gets_an_empty_hidden_folder_only_this_user_can_open_beside_the_output()
    {
        string? handed = null;
        var mode = UnixFileMode.None;
        var entries = Array.Empty<string>();

        await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), (temp, _) =>
        {
            handed = temp;
            mode = File.GetUnixFileMode(temp);
            entries = Directory.GetFileSystemEntries(temp);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(_dir.Path, Path.GetDirectoryName(handed));
        Assert.StartsWith(".", Path.GetFileName(handed), StringComparison.Ordinal);
        Assert.Equal(OwnerOnly, mode);
        Assert.Empty(entries);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_finished_folder_gets_the_permissions_of_any_new_folder()
    {
        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), WritePages(2), CancellationToken.None);

        var fresh = Directory.CreateDirectory(_dir.Combine("fresh")).FullName;
        Assert.Equal(File.GetUnixFileMode(fresh), File.GetUnixFileMode(final));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Cleanup_removes_a_symbolic_link_left_in_the_folder_without_following_it()
    {
        var outside = Directory.CreateDirectory(_dir.Combine("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");

        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
        {
            await WritePages(1)(temp, ct);
            Directory.CreateSymbolicLink(Path.Combine(temp, "link"), outside);
            throw new InvalidOperationException("boom");
        }, CancellationToken.None));

        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
        Assert.Equal(["outside"], EntryNames(_dir.Path));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_output_goes_in_the_real_folder_even_if_a_symbolic_link_above_is_repointed()
    {
        var real = Directory.CreateDirectory(_dir.Combine("real")).FullName;
        var other = Directory.CreateDirectory(_dir.Combine("other")).FullName;
        var via = _dir.Combine("via");
        Directory.CreateSymbolicLink(via, real);

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(Path.Combine(via, "a (pages)"), async (temp, ct) =>
        {
            await WritePages(1)(temp, ct);
            Directory.Delete(via);
            Directory.CreateSymbolicLink(via, other);
        }, CancellationToken.None);

        Assert.Equal(Path.Combine(real, "a (pages)"), final);
        Assert.Equal(["a (pages)"], EntryNames(real));
        Assert.Empty(EntryNames(other));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_temporary_folder_swapped_for_a_symbolic_link_is_neither_renamed_nor_followed()
    {
        var outside = Directory.CreateDirectory(_dir.Combine("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep me");

        await Assert.ThrowsAsync<IOException>(() => OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
        {
            await WritePages(1)(temp, ct);
            Directory.Move(temp, _dir.Combine("moved away"));
            Directory.CreateSymbolicLink(temp, outside);
        }, CancellationToken.None));

        Assert.False(Path.Exists(_dir.Combine("a (pages)")));
        Assert.Equal(["precious.txt"], EntryNames(outside));
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_dangling_symbolic_link_occupies_the_name_it_has()
    {
        File.CreateSymbolicLink(_dir.Combine("a (copy).txt"), _dir.Combine("nowhere"));
        Directory.CreateSymbolicLink(_dir.Combine("a (pages)"), _dir.Combine("nowhere either"));

        var file = await OutputPath.WriteAtomicallyAsync(_dir.Combine("a (copy).txt"), (temp, ct) => File.WriteAllTextAsync(temp, "x", ct), CancellationToken.None);
        var folder = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), WritePages(1), CancellationToken.None);

        Assert.Equal(_dir.Combine("a (copy) (1).txt"), file);
        Assert.Equal(_dir.Combine("a (pages) (1)"), folder);
        Assert.False(Path.Exists(_dir.Combine("nowhere")) || Path.Exists(_dir.Combine("nowhere either")));
    }

    // The name is checked before the rename, so only a folder made in the moment between reaches the rename itself.
    [LinuxTheory]
    [SupportedOSPlatform("linux")]
    [InlineData(true)]
    [InlineData(false)]
    public void The_rename_never_replaces_what_took_the_name_in_the_meantime(bool folderTookIt)
    {
        using var folder = UnixPartialFolder.Create(_dir.Combine(".partial"));
        File.WriteAllText(Path.Combine(folder.Path, "1.txt"), "page 1");
        var taken = _dir.Combine("a (pages)");
        if (folderTookIt)
            Directory.CreateDirectory(taken);
        else
            File.WriteAllText(taken, "theirs");

        var error = folder.TryRenameTo(taken);

        Assert.NotEqual(0, error);
        Assert.Equal(_dir.Combine(".partial"), folder.Path);
        Assert.Equal(["1.txt"], EntryNames(folder.Path));
        if (folderTookIt)
            Assert.Empty(EntryNames(taken));
        else
            Assert.Equal("theirs", File.ReadAllText(taken));
    }

    private static Func<string, CancellationToken, Task> WritePages(int count) => async (folder, ct) =>
    {
        for (var page = 1; page <= count; page++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"{page}.txt"), $"page {page}", ct);
    };

    private static string[] EntryNames(string folder) =>
        Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
}
