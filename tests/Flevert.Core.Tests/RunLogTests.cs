namespace Flevert.Core.Tests;

public sealed class RunLogTests : IDisposable
{
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_logs.Path))
            File.SetAttributes(f, FileAttributes.Normal);
        _logs.Dispose();
    }

    [Fact]
    public void Write_never_throws_when_the_log_file_is_read_only()
    {
        var log = new RunLog(_logs.Path);
        File.WriteAllText(log.CurrentFile, "");
        File.SetAttributes(log.CurrentFile, FileAttributes.ReadOnly);

        log.Write("dev.copy", new FileResult("a.txt", "b.txt", null), TimeSpan.Zero);
    }

    [Fact]
    public void Write_never_throws_when_the_log_directory_cannot_be_created()
    {
        var blocker = _logs.File("not-a-directory");

        new RunLog(Path.Combine(blocker, "logs")).Write("dev.copy", new FileResult("a.txt", "b.txt", null), TimeSpan.Zero);
    }

    [Fact]
    public async Task Write_waits_for_another_writer_and_then_appends()
    {
        var log = new RunLog(_logs.Path);
        Directory.CreateDirectory(_logs.Path);
        var holder = new FileStream(log.CurrentFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        // A dedicated thread, not a thread-pool timer: on a busy CI runner the pool was starved for longer than Write's
        // 2 s wait, so the file was still held when Write gave up.
        var release = new Thread(() =>
        {
            Thread.Sleep(300);
            holder.Dispose();
        });
        release.Start();

        await Task.Run(() => log.Write("dev.copy", new FileResult("a.txt", "b.txt", null), TimeSpan.Zero));
        release.Join();

        Assert.Single(File.ReadAllLines(log.CurrentFile));
    }

    [Fact]
    public void A_log_held_open_by_a_viewer_delays_the_batch_at_most_once()
    {
        // Spreadsheet apps open a tab-separated log without write sharing and keep it open.
        var log = new RunLog(_logs.Path);
        Directory.CreateDirectory(_logs.Path);
        using var viewer = new FileStream(log.CurrentFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
        log.Write("dev.copy", new FileResult("a.txt", "b.txt", null), TimeSpan.Zero);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
            log.Write("dev.copy", new FileResult("a.txt", "b.txt", null), TimeSpan.Zero);

        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(500), $"took {timer.Elapsed}");
    }

    [Fact]
    public void Prune_never_throws_when_an_old_log_cannot_be_deleted()
    {
        var old = _logs.File("flevert-20200101.log");
        File.SetAttributes(old, FileAttributes.ReadOnly);

        new RunLog(_logs.Path).Prune(TimeSpan.FromDays(7), today: new DateOnly(2026, 10, 19));
    }

    [Fact]
    public void Bidi_overrides_and_unicode_line_separators_are_stripped()
    {
        var log = new RunLog(_logs.Path);

        log.Write("dev.copy", new FileResult("a\u202Etxt.exe\u2028b\u2029c.txt", "out.txt", null), TimeSpan.Zero);

        var input = Assert.Single(File.ReadAllLines(log.CurrentFile)).Split('\t')[3];
        Assert.DoesNotContain(input, c => c is '\u202E' or '\u2028' or '\u2029');
    }

    [Fact]
    public void Control_characters_in_any_field_cannot_forge_extra_log_lines()
    {
        var log = new RunLog(_logs.Path);

        log.Write("dev.copy\tx", new FileResult("evil\r\n2026\tfake\tok.txt", "out\n.txt", null), TimeSpan.Zero);

        var line = Assert.Single(File.ReadAllLines(log.CurrentFile));
        Assert.Equal(7, line.Split('\t').Length);
    }
}
