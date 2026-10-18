namespace ConvertAnything.Core.Tests;

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
}
