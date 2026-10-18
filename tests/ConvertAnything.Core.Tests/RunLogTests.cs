namespace ConvertAnything.Core.Tests;

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
        _ = Task.Delay(300).ContinueWith(_ => holder.Dispose());

        await Task.Run(() => log.Write("dev.copy", new FileResult("a.txt", "b.txt", null), TimeSpan.Zero));

        Assert.Single(File.ReadAllLines(log.CurrentFile));
    }

    [Fact]
    public void Prune_never_throws_when_an_old_log_cannot_be_deleted()
    {
        var old = _logs.File("ca-20200101.log");
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
