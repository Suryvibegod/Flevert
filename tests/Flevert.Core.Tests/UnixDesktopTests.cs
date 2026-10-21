using System.Diagnostics;

namespace Flevert.Core.Tests;

/// <summary>
/// The Linux desktop side of a batch: the progress window (zenity, a process of its own) and the notification at the end
/// (org.freedesktop.Notifications through gdbus), driven here through stand-in scripts that record what they get.
/// </summary>
public sealed class UnixDesktopTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_progress_window_shows_the_bar_and_the_text_it_is_given()
    {
        var zenity = FakeZenity("cat > \"$OUT\"");

        using (var window = ZenityProgress.Start("Convert to JPG", zenity))
        {
            Assert.NotNull(window);
            window.Report(new BatchProgress(3, 10, "/x/ảnh <1> & 2.heic"), "/x/ảnh <1> & 2.heic");
            await Task.Delay(200);
        }

        var lines = await WaitForLines(_dir.Combine("out"));
        Assert.Contains("--progress", Arguments(), StringComparer.Ordinal);
        Assert.Contains("--title=Flevert: Convert to JPG", Arguments(), StringComparer.Ordinal);
        Assert.Contains("30", lines);
        // zenity reads markup: the file name's < and & must show as they are.
        Assert.Contains("# 3 of 10 done: ảnh &lt;1&gt; &amp; 2.heic", lines);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Cancel_in_the_window_is_reported()
    {
        var zenity = FakeZenity("read -r line; exit 1"); // as zenity does when Cancel is pressed

        using var window = ZenityProgress.Start("Compress", zenity)!;
        window.Report(new BatchProgress(0, 2, "/x/a.jpg"), "/x/a.jpg");

        await window.Cancelled.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Closing_the_window_from_flevert_is_not_a_cancel()
    {
        var zenity = FakeZenity("cat > /dev/null");
        var window = ZenityProgress.Start("Compress", zenity)!;

        window.Dispose();

        await Task.Delay(500);
        Assert.False(window.Cancelled.IsCompleted);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Without_zenity_there_is_no_window_and_no_error()
    {
        Assert.Null(ZenityProgress.Start("Compress", _dir.Combine("missing")));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_notification_goes_to_the_desktop_with_its_text_quoted_and_escaped()
    {
        var gdbus = FakeTool("gdbus", "printf '%s\\n' \"$@\" > \"$OUT\"");

        await DesktopNotification.ShowAsync(new BatchSummary("Done: Đà Lạt's <b>photo</b> & co.pdf", false, false), gdbus);

        var arguments = await WaitForLines(_dir.Combine("out"));
        Assert.Equal(["call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications",
            "--method", "org.freedesktop.Notifications.Notify", "'Flevert'", "uint32 0", "'image-x-generic'", "'Flevert'",
            "'Done: Đà Lạt\\'s &lt;b&gt;photo&lt;/b&gt; &amp; co.pdf'", "@as []", "@a{sv} {}", "int32 6000"], arguments);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_notification_about_failures_is_a_warning()
    {
        var gdbus = FakeTool("gdbus", "printf '%s\\n' \"$@\" > \"$OUT\"");

        await DesktopNotification.ShowAsync(new BatchSummary("Failed: a.pdf: damaged.\nDetails: /logs", true, true), gdbus);

        var arguments = await WaitForLines(_dir.Combine("out"));
        Assert.Equal("'dialog-warning'", arguments[10]);
        Assert.Equal("'Failed: a.pdf: damaged.\\nDetails: /logs'", arguments[12]);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Without_a_desktop_to_notify_nothing_fails()
    {
        await DesktopNotification.ShowAsync(new BatchSummary("Done.", false, false), FakeTool("gdbus", "exit 1"));
        await DesktopNotification.ShowAsync(new BatchSummary("Done.", false, false), _dir.Combine("missing"));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_quick_batch_shows_no_window_and_one_notification()
    {
        var zenity = FakeZenity("cat > /dev/null");
        var gdbus = FakeTool("gdbus", "printf '%s\\n' \"$@\" > \"$OUT\"");

        var ok = UnixBatchUi.Run("Copy", new RunLog(_dir.Combine("logs")), (_, _) => Task.FromResult(Done("/x/a (copy).txt")), zenity, gdbus);

        Assert.True(ok);
        Assert.False(File.Exists(_dir.Combine("args")), "the window was shown for a quick batch");
        Assert.Contains("'Done: a (copy).txt'", await WaitForLines(_dir.Combine("out")));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_slow_batch_shows_the_window_until_it_ends()
    {
        var zenity = FakeZenity("cat > \"$OUT.window\"");
        var gdbus = FakeTool("gdbus", "printf '%s\\n' \"$@\" > \"$OUT\"");

        var ok = UnixBatchUi.Run("Copy", new RunLog(_dir.Combine("logs")), async (progress, ct) =>
        {
            progress?.Report(new BatchProgress(0, 1, "/x/a.txt"));
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return Done("/x/a (copy).txt");
        }, zenity, gdbus);

        Assert.True(ok);
        Assert.Contains("--progress", Arguments(), StringComparer.Ordinal);
        Assert.Contains("# 0 of 1 done: a.txt", await WaitForLines(_dir.Combine("out.window")));
        Assert.Contains("'Done: a (copy).txt'", await WaitForLines(_dir.Combine("out")));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Cancel_in_the_window_stops_the_batch_and_says_so()
    {
        var zenity = FakeZenity("read -r line; exit 1");
        var gdbus = FakeTool("gdbus", "printf '%s\\n' \"$@\" > \"$OUT\"");
        var stopped = false;

        var ok = UnixBatchUi.Run("Copy", new RunLog(_dir.Combine("logs")), async (progress, ct) =>
        {
            progress?.Report(new BatchProgress(0, 2, "/x/a.txt"));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                stopped = true;
            }

            return new BatchOutcome([], 2);
        }, zenity, gdbus);

        Assert.False(ok);
        Assert.True(stopped);
        Assert.Contains("'Cancelled after 0 of 2 files.'", await WaitForLines(_dir.Combine("out")));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void An_open_question_holds_the_window_back_and_hears_the_progress()
    {
        var zenity = FakeZenity("cat > /dev/null");
        var gdbus = FakeTool("gdbus", "exit 0");
        var question = new FakeQuestion { Asking = true };

        UnixBatchUi.Run("Protect", new RunLog(_dir.Combine("logs")), async (progress, ct) =>
        {
            progress?.Report(new BatchProgress(0, 3, "/x/a.pdf"));
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            question.Asking = false; // answered: the window may show 2 s from now, after the batch has ended
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            return Done("/x/a (protected).pdf");
        }, zenity, gdbus, question);

        Assert.False(File.Exists(_dir.Combine("args")), "the window covered the question");
        Assert.Equal(3, question.Last?.Total);
    }

    private sealed class FakeQuestion : IUnixQuestion
    {
        public bool Asking { get; set; }

        public BatchProgress? Last { get; private set; }

        public void Report(BatchProgress progress) => Last = progress;
    }

    private static BatchOutcome Done(string output) => new([new FileResult("/x/a.txt", output, null)], 1);

    [SupportedOSPlatform("linux")]
    private string FakeZenity(string body) => FakeTool("zenity", $"printf '%s\\n' \"$@\" > \"{_dir.Combine("args")}\"; {body}");

    [SupportedOSPlatform("linux")]
    private string FakeTool(string name, string body)
    {
        var path = _dir.Combine(name);
        File.WriteAllText(path, $"#!/bin/sh\nOUT='{_dir.Combine("out")}'\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string[] Arguments() => File.ReadAllLines(_dir.Combine("args"));

    private static async Task<string[]> WaitForLines(string path)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(path) && File.ReadAllText(path).EndsWith('\n'))
                return File.ReadAllLines(path);
            await Task.Delay(20);
        }

        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }
}
