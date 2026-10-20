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

    // An empty folder can be turned into a junction in place by anyone who can write to it; a hidden file held open without
    // sharing keeps it from ever being empty, and is gone before the folder gets its name.
    [Fact]
    public async Task WriteDirectoryAtomically_hands_over_a_fixed_length_folder_beside_the_output_holding_only_its_hidden_anchor()
    {
        string? handed = null;
        string[]? entries = null;
        var anchorHidden = false;

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("ảnh 1 (pages)"), (temp, ct) =>
        {
            handed = temp;
            entries = EntryNames(temp);
            anchorHidden = File.GetAttributes(Path.Combine(temp, PartialFolder.AnchorName)).HasFlag(FileAttributes.Hidden);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(_dir.Path, Path.GetDirectoryName(handed));
        Assert.Matches(@"^\.flevert-[0-9a-f]{32}\.partial$", Path.GetFileName(handed));
        Assert.Equal([PartialFolder.AnchorName], entries!);
        Assert.True(anchorHidden);
        Assert.Empty(EntryNames(final));
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
                // An empty folder is removed; one with files in it is left alone (see the test on folders found inside).
                Directory.CreateDirectory(Path.Combine(temp, "sub"));
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

    // Another account with write access to the folder beside the input (a second NTFS drive gives Authenticated Users
    // Modify by default) renames the temporary folder away and puts a junction to the victim's files under its name, then
    // waits for a page to fail. A rename from another process is refused the same way as one from this process: sharing
    // is checked per handle, whichever process holds it.
    [Fact]
    public async Task WriteDirectoryAtomically_cleanup_does_not_follow_a_junction_swapped_in_for_the_temporary_folder()
    {
        using var victim = new TempDir();
        var precious = victim.File("keep.txt", "precious");
        var movedAway = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPath.WriteDirectoryAtomicallyAsync(
            _dir.Combine("a (pages)"),
            async (temp, ct) =>
            {
                await WritePages(1)(temp, ct);
                try
                {
                    Directory.Move(temp, temp + "-away");
                    movedAway = true;
                }
                catch (IOException) { }
                Junction.TryCreate(temp, victim.Path);
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None));

        Assert.Equal("precious", await File.ReadAllTextAsync(precious));
        Assert.False(movedAway, "the temporary folder could be renamed while it was being written");
        Assert.Empty(EntryNames(_dir.Path));
    }

    // Holding the folder locks the chain of real directories above it, but not a junction higher in the spelling the
    // caller gave: that resolves away and can be re-pointed. If the output name were built from that spelling, the finished
    // pages would be renamed into the attacker's tree. The folder is resolved to its real path when it is held, and every
    // path after that (writer, listing, deletes, the rename destination) uses the real path, so the junction is out of play.
    [SkippableFact]
    public async Task WriteDirectoryAtomically_puts_the_output_in_the_real_folder_even_if_a_junction_above_is_repointed()
    {
        var real = Directory.CreateDirectory(_dir.Combine("real")).FullName;
        Directory.CreateDirectory(Path.Combine(real, "sub"));
        using var attacker = new TempDir();
        Directory.CreateDirectory(Path.Combine(attacker.Path, "sub"));
        var link = _dir.Combine("link");
        Skip.IfNot(Junction.TryCreate(link, real), "could not create a junction");

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(Path.Combine(link, "sub", "out (pages)"), " (pages)", async (temp, ct) =>
        {
            await WritePages(2)(temp, ct);
            Directory.Delete(link); // remove the junction (not its target) and re-point it at the attacker's tree
            Junction.TryCreate(link, attacker.Path);
        }, CancellationToken.None);

        Assert.Equal(["out (pages)"], EntryNames(Path.Combine(real, "sub")));
        Assert.Equal(["1.txt", "2.txt"], EntryNames(Path.Combine(real, "sub", "out (pages)")));
        Assert.Equal(Path.Combine(real, "sub", "out (pages)"), final);
        // Nothing of the user's output reached the attacker's tree.
        Assert.Empty(EntryNames(Path.Combine(attacker.Path, "sub")));
    }

    // The same account cannot rename the folder, but it can open it for FILE_WRITE_ATTRIBUTES (not subject to sharing) and
    // turn it into a junction in place, which only an empty folder allows: before the first page it is empty.
    [Fact]
    public async Task WriteDirectoryAtomically_cleanup_does_not_follow_the_temporary_folder_turned_into_a_junction()
    {
        using var victim = new TempDir();
        var precious = victim.File("keep.txt", "precious");
        var converted = -1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPath.WriteDirectoryAtomicallyAsync(
            _dir.Combine("a (pages)"),
            async (temp, ct) =>
            {
                converted = Junction.TryConvert(temp, victim.Path);
                await WritePages(1)(temp, ct);
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None));

        Assert.Equal(["keep.txt"], EntryNames(victim.Path));
        Assert.Equal("precious", await File.ReadAllTextAsync(precious));
        Assert.NotEqual(0, converted);
        Assert.Empty(EntryNames(_dir.Path));
    }

    [Fact]
    public async Task WriteDirectoryAtomically_pages_cannot_be_redirected_by_turning_the_temporary_folder_into_a_junction()
    {
        using var victim = new TempDir();
        victim.File("keep.txt", "precious");
        var converted = -1;

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
        {
            converted = Junction.TryConvert(temp, victim.Path);
            await WritePages(2)(temp, ct);
        }, CancellationToken.None);

        Assert.Equal(["keep.txt"], EntryNames(victim.Path));
        Assert.False(File.GetAttributes(final).HasFlag(FileAttributes.ReparsePoint), Junction.Describe(converted));
        Assert.Equal(["1.txt", "2.txt"], EntryNames(final));
    }

    // Renaming a folder fails with "access denied" while any file in it is open, even with delete sharing: an antivirus
    // scan, the search indexer, OneDrive or Explorer making a thumbnail must not cost a 200-page render at the very end.
    [Fact]
    public async Task WriteDirectoryAtomically_waits_for_another_program_to_close_a_file_in_the_folder()
    {
        Task? closed = null;

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
        {
            await WritePages(2)(temp, ct);
            var scanner = new FileStream(Path.Combine(temp, "1.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            closed = Task.Run(async () =>
            {
                await Task.Delay(300, CancellationToken.None);
                await scanner.DisposeAsync();
            }, CancellationToken.None);
        }, CancellationToken.None);
        await closed!;

        Assert.Equal(_dir.Combine("a (pages)"), final);
        Assert.Equal(["1.txt", "2.txt"], EntryNames(final));
        Assert.Equal(["a (pages)"], EntryNames(_dir.Path));
    }

    // A full scan of a large page by an antivirus or a sync app holding it open can take several seconds: a short wait
    // would still lose a long render at the very end.
    [Fact]
    public async Task WriteDirectoryAtomically_waits_several_seconds_for_another_program_to_close_a_file()
    {
        Task? closed = null;

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
        {
            await WritePages(2)(temp, ct);
            var scanner = new FileStream(Path.Combine(temp, "1.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            closed = Task.Run(async () =>
            {
                await Task.Delay(3000, CancellationToken.None);
                await scanner.DisposeAsync();
            }, CancellationToken.None);
        }, CancellationToken.None);
        await closed!;

        Assert.Equal(_dir.Combine("a (pages)"), final);
        Assert.Equal(["1.txt", "2.txt"], EntryNames(final));
    }

    // The wait is bounded. Giving up must not cost the pages: they are complete, only the name is missing, so the folder
    // is kept, visible, and the error says where it is.
    [Fact]
    public async Task WriteDirectoryAtomically_keeps_the_pages_when_a_file_stays_open_past_the_retry_window()
    {
        FileStream? scanner = null;
        var wait = TimeSpan.FromMilliseconds(300);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), "", async (temp, ct) =>
            {
                await WritePages(2)(temp, ct);
                scanner = new FileStream(Path.Combine(temp, "1.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }, wait, CancellationToken.None));
            timer.Stop();

            var kept = Assert.Single(EntryNames(_dir.Path));
            Assert.Matches(@"^\.flevert-[0-9a-f]{32}\.partial$", kept);
            Assert.Contains(kept, error.Message, StringComparison.Ordinal);
            Assert.Equal(["1.txt", "2.txt"], EntryNames(_dir.Combine(kept)));
            Assert.False(File.GetAttributes(_dir.Combine(kept)).HasFlag(FileAttributes.Hidden));
            Assert.Contains("a (pages)", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            scanner?.Dispose();
        }

        Assert.True(timer.Elapsed >= wait, $"{timer.Elapsed}");
    }

    [Fact]
    public async Task WriteDirectoryAtomically_stops_waiting_for_the_rename_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        FileStream? scanner = null;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
            {
                await WritePages(2)(temp, ct);
                scanner = new FileStream(Path.Combine(temp, "1.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                cts.CancelAfter(200);
            }, cts.Token));
            timer.Stop();

            Assert.Empty(EntryNames(_dir.Path));
        }
        finally
        {
            scanner?.Dispose();
        }

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1.5), $"{timer.Elapsed}");
    }

    // Out of sight while it is written, so Explorer does not open its pages for a folder thumbnail, and out of the search
    // index; both would hold pages open and delay the rename.
    [Fact]
    public async Task WriteDirectoryAtomically_hides_the_temporary_folder_from_view_and_indexing_but_not_the_result()
    {
        File.SetAttributes(_dir.Path, File.GetAttributes(_dir.Path) & ~FileAttributes.NotContentIndexed);
        FileAttributes during = 0;

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
        {
            during = File.GetAttributes(temp);
            await WritePages(2)(temp, ct);
        }, CancellationToken.None);

        Assert.True(during.HasFlag(FileAttributes.Hidden), $"{during}");
        Assert.True(during.HasFlag(FileAttributes.NotContentIndexed), $"{during}");
        var after = File.GetAttributes(final);
        Assert.False(after.HasFlag(FileAttributes.Hidden), $"{after}");
        Assert.False(after.HasFlag(FileAttributes.NotContentIndexed), $"{after}");
        // A file created in a folder marked NotContentIndexed inherits the mark: the pages must be indexed like any file.
        Assert.All(Directory.GetFiles(final), page =>
            Assert.False(File.GetAttributes(page).HasFlag(FileAttributes.NotContentIndexed), page));
    }

    // Hiding and readying the folder replace its attributes through FileBasicInfo. OneDrive's "Always keep on this device"
    // (FILE_ATTRIBUTE_PINNED) is an attribute set the same way: a page or folder created in a pinned OneDrive folder carries
    // it, and dropping it would let OneDrive free the output's space. Only the bits being changed may change.
    [SkippableFact]
    public async Task WriteDirectoryAtomically_keeps_attributes_it_does_not_change_such_as_onedrive_pinning()
    {
        const FileAttributes pinned = (FileAttributes)0x80000;
        string? page = null;

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
        {
            await WritePages(1)(temp, ct);
            File.SetAttributes(temp, File.GetAttributes(temp) | pinned);
            page = Path.Combine(temp, "1.txt");
            File.SetAttributes(page, File.GetAttributes(page) | pinned | FileAttributes.NotContentIndexed);
            Skip.IfNot(File.GetAttributes(temp).HasFlag(pinned), "this volume does not keep FILE_ATTRIBUTE_PINNED");
        }, CancellationToken.None);

        Assert.True(File.GetAttributes(final).HasFlag(pinned), $"{File.GetAttributes(final)}");
        var after = File.GetAttributes(Path.Combine(final, "1.txt"));
        Assert.True(after.HasFlag(pinned), $"{after}");
        Assert.False(after.HasFlag(FileAttributes.NotContentIndexed), $"{after}");
    }

    // Guard for the hiding test: a folder the user excluded from indexing keeps its children excluded.
    [Fact]
    public async Task WriteDirectoryAtomically_keeps_the_result_out_of_indexing_when_the_folder_beside_it_is()
    {
        File.SetAttributes(_dir.Path, File.GetAttributes(_dir.Path) | FileAttributes.NotContentIndexed);

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), WritePages(2), CancellationToken.None);

        Assert.True(File.GetAttributes(final).HasFlag(FileAttributes.NotContentIndexed));
        Assert.All(Directory.GetFiles(final), page =>
            Assert.True(File.GetAttributes(page).HasFlag(FileAttributes.NotContentIndexed), page));
    }

    // Readying pages for the rename clears their inherited NotContentIndexed mark. A hard link someone planted points at a
    // file outside the folder; changing attributes through it would reach that file, so a multiply-linked entry is skipped.
    [Fact]
    public async Task WriteDirectoryAtomically_does_not_change_attributes_through_a_planted_hard_link()
    {
        using var outside = new TempDir();
        var external = outside.File("external.txt", "theirs");
        File.SetAttributes(external, File.GetAttributes(external) | FileAttributes.NotContentIndexed);

        await OutputPath.WriteDirectoryAtomicallyAsync(_dir.Combine("a (pages)"), async (temp, ct) =>
        {
            await WritePages(1)(temp, ct);
            Junction.TryCreateHardLink(Path.Combine(temp, "planted.txt"), external);
        }, CancellationToken.None);

        // The external file keeps its mark: the hard link inside the output folder was left alone.
        Assert.True(File.GetAttributes(external).HasFlag(FileAttributes.NotContentIndexed));
    }

    // Writers never make folders (PdfToImages, DevSplit), so a folder with something in it was put there by someone else,
    // and going into it by path could be redirected the same way as the temporary folder itself.
    [Fact]
    public async Task WriteDirectoryAtomically_cleanup_never_goes_into_a_folder_it_finds_inside()
    {
        string? handed = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPath.WriteDirectoryAtomicallyAsync(
            _dir.Combine("a (pages)"),
            async (temp, ct) =>
            {
                handed = temp;
                await WritePages(1)(temp, ct);
                Directory.CreateDirectory(Path.Combine(temp, "sub"));
                await File.WriteAllTextAsync(Path.Combine(temp, "sub", "deep.txt"), "theirs", ct);
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None));

        Assert.Equal("theirs", await File.ReadAllTextAsync(Path.Combine(handed!, "sub", "deep.txt")));
        Assert.Equal(["sub"], EntryNames(handed!));
    }

    // What the cleanup has to leave (someone else's folder inside, a file it could not delete) must not stay hidden beside
    // the input, where the user would never find it.
    [Fact]
    public async Task WriteDirectoryAtomically_shows_a_temporary_folder_the_cleanup_could_not_remove()
    {
        File.SetAttributes(_dir.Path, File.GetAttributes(_dir.Path) & ~FileAttributes.NotContentIndexed);
        string? handed = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPath.WriteDirectoryAtomicallyAsync(
            _dir.Combine("a (pages)"),
            async (temp, ct) =>
            {
                handed = temp;
                await WritePages(1)(temp, ct);
                Directory.CreateDirectory(Path.Combine(temp, "sub"));
                await File.WriteAllTextAsync(Path.Combine(temp, "sub", "deep.txt"), "theirs", ct);
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None));

        var left = File.GetAttributes(handed!);
        Assert.False(left.HasFlag(FileAttributes.Hidden), $"{left}");
        Assert.False(left.HasFlag(FileAttributes.NotContentIndexed), $"{left}");
    }

    // Guard: the folder is now created, held, renamed and deleted through Win32 calls rather than .NET's path APIs.
    [Fact]
    public async Task WriteDirectoryAtomically_handles_paths_longer_than_260_characters()
    {
        var deep = _dir.Path;
        while (deep.Length < 300)
            deep = Path.Combine(deep, "thư mục rất dài " + new string('x', 40));
        Directory.CreateDirectory(Path.Combine(deep, "a (pages)"));

        var final = await OutputPath.WriteDirectoryAtomicallyAsync(Path.Combine(deep, "a (pages)"), WritePages(2), CancellationToken.None);

        Assert.Equal(Path.Combine(deep, "a (pages) (1)"), final);
        Assert.Equal(["1.txt", "2.txt"], EntryNames(final));
        Assert.Equal(["a (pages)", "a (pages) (1)"], EntryNames(deep));
    }

    private static Func<string, CancellationToken, Task> WritePages(int count) => async (folder, ct) =>
    {
        for (var page = 1; page <= count; page++)
            await File.WriteAllTextAsync(Path.Combine(folder, $"{page}.txt"), $"page {page}", ct);
    };

    private static string[] EntryNames(string folder) =>
        Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
}
