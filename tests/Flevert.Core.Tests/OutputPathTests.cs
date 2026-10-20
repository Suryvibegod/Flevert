namespace Flevert.Core.Tests;

public sealed class OutputPathTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Reserve_returns_desired_path_when_free()
    {
        var desired = _dir.Combine("ảnh 1.png");

        Assert.Equal(desired, OutputPath.Reserve(desired));
    }

    [Fact]
    public void Reserve_appends_counter_when_desired_path_exists()
    {
        _dir.File("ảnh 1.png");

        Assert.Equal(_dir.Combine("ảnh 1 (1).png"), OutputPath.Reserve(_dir.Combine("ảnh 1.png")));
    }

    [Fact]
    public void Reserve_skips_counters_already_taken()
    {
        _dir.File("a.png");
        _dir.File("a (1).png");
        _dir.File("a (2).png");

        Assert.Equal(_dir.Combine("a (3).png"), OutputPath.Reserve(_dir.Combine("a.png")));
    }

    [Fact]
    public void Reserve_treats_a_directory_with_the_desired_name_as_taken()
    {
        Directory.CreateDirectory(_dir.Combine("a.png"));

        Assert.Equal(_dir.Combine("a (1).png"), OutputPath.Reserve(_dir.Combine("a.png")));
    }

    [Fact]
    public void Reserve_shortens_the_name_so_the_counter_still_fits_in_255_characters()
    {
        var name = new string('n', 251) + ".txt";
        _dir.File(name);

        var reserved = Path.GetFileName(OutputPath.Reserve(_dir.Combine(name)));

        Assert.True(reserved.Length <= 255, $"{reserved.Length} characters");
        Assert.EndsWith(" (1).txt", reserved, StringComparison.Ordinal);
    }

    [Fact]
    public void Reserve_reports_a_clear_error_when_the_extension_leaves_no_room_for_a_counter()
    {
        var desired = _dir.File("a." + new string('e', 252)); // 254 characters: valid, but " (1)" cannot fit

        Assert.Throws<PathTooLongException>(() => OutputPath.Reserve(desired));
    }

    [Fact]
    public async Task WriteAtomically_skips_a_dangling_link_that_occupies_the_desired_name()
    {
        var target = Directory.CreateDirectory(_dir.Combine("gone")).FullName;
        var link = _dir.Combine("out.txt");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        await mklink.WaitForExitAsync();
        Directory.Delete(target); // the junction now dangles
        try
        {
            var final = await OutputPath.WriteAtomicallyAsync(link, (temp, ct) => File.WriteAllTextAsync(temp, "x", ct), CancellationToken.None);

            Assert.Equal(_dir.Combine("out (1).txt"), final);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task WriteAtomically_produces_final_file_and_leaves_no_partial()
    {
        var final = await OutputPath.WriteAtomicallyAsync(
            _dir.Combine("out.txt"),
            (temp, ct) => File.WriteAllTextAsync(temp, "hello", ct),
            CancellationToken.None);

        Assert.Equal(_dir.Combine("out.txt"), final);
        Assert.Equal("hello", await File.ReadAllTextAsync(final));
        Assert.Equal(["out.txt"], _dir.FileNames());
    }

    [Fact]
    public async Task WriteAtomically_leaves_nothing_behind_when_writer_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPath.WriteAtomicallyAsync(
            _dir.Combine("out.txt"),
            async (temp, ct) =>
            {
                await File.WriteAllTextAsync(temp, "half", ct);
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None));

        Assert.Empty(_dir.FileNames());
    }

    [Fact]
    public async Task WriteAtomically_leaves_nothing_behind_when_cancelled()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OutputPath.WriteAtomicallyAsync(
            _dir.Combine("out.txt"),
            async (temp, ct) =>
            {
                await File.WriteAllTextAsync(temp, "half", ct);
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
            },
            cts.Token));

        Assert.Empty(_dir.FileNames());
    }

    [Fact]
    public async Task WriteAtomically_does_not_overwrite_file_created_during_write()
    {
        var final = await OutputPath.WriteAtomicallyAsync(
            _dir.Combine("out.txt"),
            async (temp, ct) =>
            {
                await File.WriteAllTextAsync(temp, "mine", ct);
                _dir.File("out.txt", "someone else");
            },
            CancellationToken.None);

        Assert.Equal(_dir.Combine("out (1).txt"), final);
        Assert.Equal("someone else", await File.ReadAllTextAsync(_dir.Combine("out.txt")));
        Assert.Equal("mine", await File.ReadAllTextAsync(final));
    }
    [Fact]
    public async Task WriteDirectoryAtomically_hands_over_an_empty_fixed_length_folder_beside_the_output()
    {
        string? handed = null;

        await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("ảnh 1 (pages)"), (temp, ct) =>
        {
            handed = temp;
            Assert.True(Directory.Exists(temp));
            Assert.Empty(Directory.EnumerateFileSystemEntries(temp));
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(_dir.Path, Path.GetDirectoryName(handed));
        Assert.Matches(@"^\.flevert-[0-9a-f]{32}\.partial$", Path.GetFileName(handed));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_produces_the_folder_with_its_files_and_leaves_no_partial()
    {
        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("ảnh 1 (pages)"), WritePages(3), CancellationToken.None);

        Assert.Equal(_dir.Combine("ảnh 1 (pages)"), final);
        Assert.Equal(["ảnh 1 (pages)"], EntryNames(_dir.Path));
        Assert.Equal(["1.txt", "2.txt", "3.txt"], EntryNames(final));
        Assert.Equal("page 2", await File.ReadAllTextAsync(Path.Combine(final, "2.txt")));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_never_merges_into_an_existing_folder()
    {
        var existing = Directory.CreateDirectory(_dir.Combine("a (pages)")).FullName;
        File.WriteAllText(Path.Combine(existing, "1.txt"), "theirs");

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(existing, WritePages(2), CancellationToken.None);

        Assert.Equal(_dir.Combine("a (pages) (1)"), final);
        Assert.Equal(["1.txt"], EntryNames(existing));
        Assert.Equal("theirs", await File.ReadAllTextAsync(Path.Combine(existing, "1.txt")));
        Assert.Equal(["1.txt", "2.txt"], EntryNames(final));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_treats_a_file_with_the_desired_name_as_taken()
    {
        _dir.File("a (pages)", "a file");
        Directory.CreateDirectory(_dir.Combine("a (pages) (1)"));

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), WritePages(1), CancellationToken.None);

        Assert.Equal(_dir.Combine("a (pages) (2)"), final);
        Assert.Equal("a file", await File.ReadAllTextAsync(_dir.Combine("a (pages)")));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_puts_the_counter_after_the_whole_folder_name_even_with_a_dot_in_it()
    {
        Directory.CreateDirectory(_dir.Combine("report.v2 (pages)"));

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("report.v2 (pages)"), WritePages(1), CancellationToken.None);

        Assert.Equal(_dir.Combine("report.v2 (pages) (1)"), final);
    }

    [Fact]
    public async Task WriteDirectoryAtomically_shortens_the_name_so_the_counter_still_fits_in_255_characters()
    {
        var name = new string('n', 255);
        Directory.CreateDirectory(_dir.Combine(name));

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine(name), WritePages(1), CancellationToken.None);

        var created = Path.GetFileName(final);
        Assert.Equal(255, created.Length);
        Assert.EndsWith("n (1)", created, StringComparison.Ordinal);
        Assert.True(Directory.Exists(final));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_takes_the_next_name_when_a_folder_appears_during_the_write()
    {
        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
        {
            await WritePages(2)(temp, ct);
            var theirs = Directory.CreateDirectory(_dir.Combine("a (pages)")).FullName;
            File.WriteAllText(Path.Combine(theirs, "1.txt"), "theirs");
        }, CancellationToken.None);

        Assert.Equal(_dir.Combine("a (pages) (1)"), final);
        Assert.Equal(["1.txt"], EntryNames(_dir.Combine("a (pages)")));
        Assert.Equal("theirs", await File.ReadAllTextAsync(Path.Combine(_dir.Combine("a (pages)"), "1.txt")));
        Assert.Equal(["1.txt", "2.txt"], EntryNames(final));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_leaves_nothing_behind_when_the_writer_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPath.WriteDirectoryAtomicallyAsync(
            _dir.Combine("a (pages)"),
            async (temp, ct) =>
            {
                await WritePages(2)(temp, ct);
                Directory.CreateDirectory(Path.Combine(temp, "sub"));
                await File.WriteAllTextAsync(Path.Combine(temp, "sub", "deep.txt"), "half", ct);
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None));

        Assert.Empty(EntryNames(_dir.Path));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_leaves_nothing_behind_when_cancelled_midway()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OutputPath.WriteDirectoryAtomicallyAsync(
            _dir.Combine("a (pages)"),
            async (temp, ct) =>
            {
                for (var page = 1; page <= 50; page++)
                {
                    await File.WriteAllTextAsync(Path.Combine(temp, $"{page}.txt"), "x", ct);
                    if (page == 10)
                        await cts.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                }
            },
            cts.Token));

        Assert.Empty(EntryNames(_dir.Path));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_leaves_nothing_behind_when_cancelled_after_the_writer_finished()
    {
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OutputPath.WriteDirectoryAtomicallyAsync(
            _dir.Combine("a (pages)"),
            async (temp, ct) =>
            {
                await WritePages(2)(temp, ct);
                await cts.CancelAsync(); // the writer itself ignores it
            },
            cts.Token));

        Assert.Empty(EntryNames(_dir.Path));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_leaves_nothing_behind_even_when_the_writer_left_a_read_only_file()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPath.WriteDirectoryAtomicallyAsync(
            _dir.Combine("a (pages)"),
            async (temp, ct) =>
            {
                await WritePages(1)(temp, ct);
                File.SetAttributes(Path.Combine(temp, "1.txt"), FileAttributes.ReadOnly);
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None));

        Assert.Empty(EntryNames(_dir.Path));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_cleanup_does_not_follow_a_junction_left_in_the_folder()
    {
        using var outside = new TempDir();
        var precious = outside.File("keep.txt", "precious");

        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPath.WriteDirectoryAtomicallyAsync(
            _dir.Combine("a (pages)"),
            async (temp, ct) =>
            {
                using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "cmd.exe", $"/c mklink /J \"{Path.Combine(temp, "link")}\" \"{outside.Path}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                })!;
                await mklink.WaitForExitAsync(ct);
                Assert.Equal(0, mklink.ExitCode);
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None));

        Assert.Empty(EntryNames(_dir.Path));
        Assert.Equal("precious", await File.ReadAllTextAsync(precious));
    }

    private static Func<string, CancellationToken, Task> WritePages(int count) => async (folder, ct) =>
    {
        for (var page = 1; page <= count; page++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"{page}.txt"), $"page {page}", ct);
    };

    private static string[] EntryNames(string folder) =>
        Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
}
