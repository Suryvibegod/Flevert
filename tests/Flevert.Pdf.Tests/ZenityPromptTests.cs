using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary>
/// The questions a batch asks on Linux (a PDF's password, a watermark's text) in zenity dialogs, driven here through a
/// stand-in that records its arguments and gives scripted answers, one per line of a file, in turn.
/// </summary>
public sealed class ZenityPromptTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Protecting_asks_for_the_password_twice_and_takes_it()
    {
        var zenity = FakeZenity(Answer(0, "s3cret ếch\u001es3cret ếch\n"));

        var answer = await new ZenityPasswordPrompt(zenity).AskAsync(new PasswordQuestion("/x/Hồ sơ.pdf", PasswordPurpose.Protect, 0), CancellationToken.None);

        using var password = answer.Password;
        Assert.Equal("s3cret ếch", new string(password!.Chars));
        Assert.Equal(1, answer.Files);
        var call = Calls().Single();
        Assert.Contains("--forms", call, StringComparer.Ordinal);
        Assert.Equal(2, call.Count(a => a.StartsWith("--add-password=", StringComparison.Ordinal)));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Removing_asks_for_the_password_once()
    {
        var zenity = FakeZenity(Answer(0, "open sesame\n"));

        var answer = await new ZenityPasswordPrompt(zenity).AskAsync(new PasswordQuestion("/x/a.pdf", PasswordPurpose.Unlock, 0), CancellationToken.None);

        using var password = answer.Password;
        Assert.Equal("open sesame", new string(password!.Chars));
        Assert.Single(Calls().Single(), a => a.StartsWith("--add-password=", StringComparison.Ordinal));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Two_passwords_that_differ_are_asked_again_saying_why()
    {
        var zenity = FakeZenity(Answer(0, "one\u001etwo\n"), Answer(0, "two\u001etwo\n"));

        var answer = await new ZenityPasswordPrompt(zenity).AskAsync(new PasswordQuestion("/x/a.pdf", PasswordPurpose.Protect, 0), CancellationToken.None);

        using var password = answer.Password;
        Assert.Equal("two", new string(password!.Chars));
        var calls = Calls();
        Assert.Equal(2, calls.Count);
        Assert.Contains(calls[1], a => a.Contains(PasswordRules.MismatchMessage, StringComparison.Ordinal));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Cancel_gives_no_password()
    {
        var zenity = FakeZenity(Answer(1, ""));

        var answer = await new ZenityPasswordPrompt(zenity).AskAsync(new PasswordQuestion("/x/a.pdf", PasswordPurpose.Unlock, 0), CancellationToken.None);

        Assert.Null(answer.Password);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task With_more_files_left_the_answer_can_be_for_all_of_them()
    {
        var zenity = FakeZenity(Answer(0, "pw\u001eAll 3 files\n"));
        var prompt = new ZenityPasswordPrompt(zenity);
        prompt.Report(new BatchProgress(1, 4, "/x/b.pdf"));

        var answer = await prompt.AskAsync(new PasswordQuestion("/x/b.pdf", PasswordPurpose.Unlock, 0), CancellationToken.None);

        using var password = answer.Password;
        Assert.Equal(3, answer.Files);
        Assert.Contains("--combo-values=All 3 files|This file only", Calls().Single(), StringComparer.Ordinal);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_password_never_reaches_a_command_line_and_the_file_name_shows_as_it_is()
    {
        var zenity = FakeZenity(Answer(0, "hunter2\n"));

        var answer = await new ZenityPasswordPrompt(zenity).AskAsync(new PasswordQuestion("/x/a <b> & c.pdf", PasswordPurpose.Unlock, 0), CancellationToken.None);

        answer.Password!.Dispose();
        var call = Calls().Single();
        Assert.DoesNotContain(call, a => a.Contains("hunter2", StringComparison.Ordinal));
        Assert.Contains(call, a => a.Contains("a &lt;b&gt; &amp; c.pdf", StringComparison.Ordinal));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task The_watermark_text_is_checked_as_on_windows_and_asked_again()
    {
        var zenity = FakeZenity(Answer(0, "\n"), Answer(0, "Chỉ dùng để nộp hồ sơ\n"));

        var answer = await new ZenityWatermarkPrompt(zenity).AskAsync(new WatermarkQuestion("/x/a.pdf", 0), CancellationToken.None);

        Assert.Equal("Chỉ dùng để nộp hồ sơ", answer.Text);
        Assert.Contains(Calls()[1], a => a.Contains(WatermarkPrompts.EmptyMessage, StringComparison.Ordinal));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task While_a_question_is_open_the_batch_knows_it()
    {
        var zenity = FakeZenity(Answer(0, "pw\n", sleep: "0.5"));
        var prompt = new ZenityPasswordPrompt(zenity);

        var asking = prompt.AskAsync(new PasswordQuestion("/x/a.pdf", PasswordPurpose.Unlock, 0), CancellationToken.None);
        await Task.Delay(200);
        var during = prompt.Asking;
        (await asking).Password!.Dispose();

        Assert.True(during);
        Assert.False(prompt.Asking);
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_backslash_in_a_name_reaches_zenity_doubled_so_it_never_becomes_markup()
    {
        // zenity runs the text through g_strcompress, where \074 is "<".
        var zenity = FakeZenity(Answer(0, "pw\n"));

        (await new ZenityPasswordPrompt(zenity).AskAsync(new PasswordQuestion("/x/a\\074b\\076.pdf", PasswordPurpose.Unlock, 0), CancellationToken.None))
            .Password!.Dispose();

        Assert.Contains(Calls().Single(), a => a.Contains("a\\\\074b\\\\076.pdf", StringComparison.Ordinal));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task A_form_with_more_or_fewer_fields_than_asked_is_asked_again()
    {
        // A line break pasted into a field cannot move the others along: fields are told apart by U+001E.
        var zenity = FakeZenity(Answer(0, "one\n"), Answer(0, "a\u001eb\u001ec\n"), Answer(0, "pw\u001epw\n"));

        var answer = await new ZenityPasswordPrompt(zenity).AskAsync(new PasswordQuestion("/x/a.pdf", PasswordPurpose.Protect, 0), CancellationToken.None);

        using var password = answer.Password;
        Assert.Equal("pw", new string(password!.Chars));
        var calls = Calls();
        Assert.Equal(3, calls.Count);
        Assert.Contains("--separator=\u001e", calls[0]);
        Assert.Contains(calls[2], a => a.Contains("Flevert could not read the answer", StringComparison.Ordinal));
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task An_answer_too_long_to_be_one_is_read_to_the_end_and_asked_again()
    {
        // Past what a pipe holds: zenity would block writing it if it were not read.
        var zenity = FakeZenity(Answer(0, new string('x', 300_000) + "\n"), Answer(0, "Bản sao\n"));

        var answer = await new ZenityWatermarkPrompt(zenity).AskAsync(new WatermarkQuestion("/x/a.pdf", 0), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("Bản sao", answer.Text);
        Assert.Equal(2, Calls().Count);
    }

    // One answer per call: exit code, then what zenity prints.
    private static (int ExitCode, string Output, string Sleep) Answer(int exitCode, string output, string sleep = "0") => (exitCode, output, sleep);

    [SupportedOSPlatform("linux")]
    private string FakeZenity(params (int ExitCode, string Output, string Sleep)[] answers)
    {
        for (var i = 0; i < answers.Length; i++)
        {
            File.WriteAllText(_dir.Combine($"answer{i}"), answers[i].Output);
            File.WriteAllText(_dir.Combine($"exit{i}"), $"{answers[i].ExitCode} {answers[i].Sleep}");
        }

        var path = _dir.Combine("zenity");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            dir='{{_dir.Path}}'
            n=$(ls "$dir" | grep -c '^call')
            printf '%s\0' "$@" > "$dir/call$n"
            read code pause < "$dir/exit$n"
            sleep "$pause"
            cat "$dir/answer$n"
            exit "$code"
            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private List<string[]> Calls() =>
        [.. Enumerable.Range(0, 10).Select(i => _dir.Combine($"call{i}")).Where(File.Exists)
            .Select(f => File.ReadAllText(f).TrimEnd('\0').Split('\0'))];
}
