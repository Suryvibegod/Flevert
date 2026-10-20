namespace Flevert.Pdf.Tests;

/// <summary>The password itself, the dialog's checks, and how one batch asks for passwords: no qpdf, no window.</summary>
public sealed class PdfPasswordTests
{
    [Fact]
    public void A_password_is_overwritten_with_zeros_when_disposed()
    {
        var chars = "Mật khẩu 🔒".ToCharArray();
        var password = new PdfPassword(chars);
        Assert.Equal("Mật khẩu 🔒", new string(password.Chars));

        password.Dispose();

        Assert.All(chars, c => Assert.Equal('\0', c));
        Assert.Throws<ObjectDisposedException>(() => password.Chars.Length);
        password.Dispose();
    }

    public static TheoryData<string, string, PasswordPurpose, string?> Entries() => new()
    {
        { "", "", PasswordPurpose.Protect, PasswordRules.EmptyMessage },
        { "", "", PasswordPurpose.Unlock, PasswordRules.EmptyMessage },
        { "abc", "abd", PasswordPurpose.Protect, PasswordRules.MismatchMessage },
        { "abc", "", PasswordPurpose.Protect, PasswordRules.MismatchMessage },
        { "abc", "abc ", PasswordPurpose.Protect, PasswordRules.MismatchMessage },
        // Unlocking asks once: the second entry is not compared.
        { "abc", "", PasswordPurpose.Unlock, null },
        { "abc", "abc", PasswordPurpose.Protect, null },
        // Spaces are part of the password, wherever they are.
        { "  two words  ", "  two words  ", PasswordPurpose.Protect, null },
        { " ", " ", PasswordPurpose.Protect, null },
        { "Mật khẩu 🔒", "Mật khẩu 🔒", PasswordPurpose.Protect, null },
        { "\"q\" 'q' @x -y =z \\w", "\"q\" 'q' @x -y =z \\w", PasswordPurpose.Protect, null },
        // 127 bytes of UTF-8 at most: plain letters take one byte, "é" two, an emoji four.
        { new string('a', 127), new string('a', 127), PasswordPurpose.Protect, null },
        { new string('a', 128), new string('a', 128), PasswordPurpose.Protect, PasswordRules.TooLongMessage },
        { new string('é', 63), "", PasswordPurpose.Unlock, null },
        { new string('é', 64), "", PasswordPurpose.Unlock, PasswordRules.TooLongMessage },
        { string.Concat(Enumerable.Repeat("🔒", 31)), "", PasswordPurpose.Unlock, null },
        { string.Concat(Enumerable.Repeat("🔒", 32)), "", PasswordPurpose.Unlock, PasswordRules.TooLongMessage },
        // One argument per line reaches qpdf; a lone surrogate has no UTF-8 form.
        { "a\nb", "a\nb", PasswordPurpose.Protect, PasswordRules.UnsupportedCharacterMessage },
        { "a\rb", "", PasswordPurpose.Unlock, PasswordRules.UnsupportedCharacterMessage },
        { "a\0b", "", PasswordPurpose.Unlock, PasswordRules.UnsupportedCharacterMessage },
    };

    [Theory]
    [MemberData(nameof(Entries))]
    public void The_dialog_lets_through_only_a_password_a_pdf_can_hold(string password, string again, PasswordPurpose purpose, string? problem)
    {
        Assert.Equal(problem, PasswordRules.Problem(password, again, purpose));
    }

    [Fact]
    public void A_lone_surrogate_has_no_utf8_form_and_is_refused()
    {
        // Not theory data: xunit's serialization of test cases turns it into U+FFFD, a character a password may hold.
        Assert.Equal(PasswordRules.UnsupportedCharacterMessage, PasswordRules.Problem("a\uD800b", "a\uD800b", PasswordPurpose.Protect));
        Assert.Equal(PasswordRules.UnsupportedCharacterMessage, PasswordRules.Problem("\uDC00", "", PasswordPurpose.Unlock));
    }

    [Fact]
    public async Task One_file_is_asked_about_once_and_its_password_is_wiped_when_it_is_done()
    {
        var chars = "pw".ToCharArray();
        var prompt = new FakePrompt(_ => new PasswordAnswer(new PdfPassword(chars), 1));
        using var batch = new BatchPasswords(prompt);

        var lease = await batch.GetAsync(@"C:\a.pdf", PasswordPurpose.Unlock, CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal("pw", new string(lease.Password.Chars));
        Assert.Equal([new PasswordQuestion(@"C:\a.pdf", PasswordPurpose.Unlock, 0)], prompt.Questions);
        lease.Dispose();
        Assert.All(chars, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task An_answer_for_all_files_serves_the_rest_without_asking_and_is_wiped_after_the_last()
    {
        var chars = "shared".ToCharArray();
        var prompt = new FakePrompt(_ => new PasswordAnswer(new PdfPassword(chars), 3));
        using var batch = new BatchPasswords(prompt);

        var leases = await Task.WhenAll(Enumerable.Range(1, 3).Select(n =>
            batch.GetAsync($@"C:\{n}.pdf", PasswordPurpose.Protect, CancellationToken.None)));

        Assert.Single(prompt.Questions);
        Assert.All(leases, lease => Assert.Equal("shared", new string(lease!.Password.Chars)));
        leases[0]!.Dispose();
        leases[2]!.Dispose();
        Assert.Equal("shared", new string(chars));
        leases[1]!.Dispose();
        Assert.All(chars, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task A_file_beyond_those_the_answer_was_for_is_asked_about_again()
    {
        var answers = new Queue<string>(["first", "second"]);
        var prompt = new FakePrompt(_ => new PasswordAnswer(TestPasswords.Make(answers.Dequeue()), 2));
        using var batch = new BatchPasswords(prompt);

        using var one = await batch.GetAsync(@"C:\1.pdf", PasswordPurpose.Protect, CancellationToken.None);
        using var two = await batch.GetAsync(@"C:\2.pdf", PasswordPurpose.Protect, CancellationToken.None);
        using var three = await batch.GetAsync(@"C:\3.pdf", PasswordPurpose.Protect, CancellationToken.None);

        Assert.Equal("first", new string(two!.Password.Chars));
        Assert.Equal("second", new string(three!.Password.Chars));
        // Two files still hold their password, so the dialog does not count them among the files left to answer for.
        Assert.Equal([new PasswordQuestion(@"C:\1.pdf", PasswordPurpose.Protect, 0), new PasswordQuestion(@"C:\3.pdf", PasswordPurpose.Protect, 2)],
            prompt.Questions);
    }

    [Fact]
    public async Task Cancel_skips_only_the_file_asked_about()
    {
        var answers = new Queue<PasswordAnswer>([new PasswordAnswer(null, 1), new PasswordAnswer(TestPasswords.Make("pw"), 1)]);
        var prompt = new FakePrompt(_ => answers.Dequeue());
        using var batch = new BatchPasswords(prompt);

        var first = await batch.GetAsync(@"C:\1.pdf", PasswordPurpose.Unlock, CancellationToken.None);
        using var second = await batch.GetAsync(@"C:\2.pdf", PasswordPurpose.Unlock, CancellationToken.None);

        Assert.Null(first);
        Assert.Equal("pw", new string(second!.Password.Chars));
        Assert.Equal(2, prompt.Questions.Count);
    }

    [Fact]
    public async Task Cancel_for_all_files_skips_them_without_asking_again()
    {
        var prompt = FakePrompt.Cancelling(files: 3);
        using var batch = new BatchPasswords(prompt);

        var leases = await Task.WhenAll(Enumerable.Range(1, 3).Select(n =>
            batch.GetAsync($@"C:\{n}.pdf", PasswordPurpose.Protect, CancellationToken.None)));
        await batch.GetAsync(@"C:\4.pdf", PasswordPurpose.Protect, CancellationToken.None);

        Assert.All(leases, Assert.Null);
        Assert.Equal(2, prompt.Questions.Count);
    }

    [Fact]
    public async Task Ending_the_batch_wipes_a_shared_password_it_was_not_all_used_for()
    {
        var chars = "pw".ToCharArray();
        var batch = new BatchPasswords(new FakePrompt(_ => new PasswordAnswer(new PdfPassword(chars), 5)));
        var lease = await batch.GetAsync(@"C:\1.pdf", PasswordPurpose.Protect, CancellationToken.None);
        lease!.Dispose();
        Assert.Equal("pw", new string(chars));

        batch.Dispose();

        Assert.All(chars, c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Only_one_question_is_open_at_a_time()
    {
        var answer = new TaskCompletionSource<PasswordAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var prompt = new BlockingPrompt(() =>
        {
            Interlocked.Increment(ref asked);
            return answer.Task;
        });
        using var batch = new BatchPasswords(prompt);

        var first = batch.GetAsync(@"C:\1.pdf", PasswordPurpose.Unlock, CancellationToken.None);
        var second = batch.GetAsync(@"C:\2.pdf", PasswordPurpose.Unlock, CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref asked));

        answer.SetResult(new PasswordAnswer(TestPasswords.Make("pw"), 2));
        using var one = await first;
        using var two = await second;
        Assert.Equal(1, Volatile.Read(ref asked));
        Assert.Same(one!.Password, two!.Password);
    }

    [Fact]
    public async Task Without_a_window_each_file_fails_at_once_with_a_clear_message()
    {
        using var batch = new BatchPasswords(PasswordPrompts.NoWindow);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            batch.GetAsync(@"C:\1.pdf", PasswordPurpose.Protect, CancellationToken.None));

        Assert.Equal(PasswordPrompts.NoWindowMessage, error.Message);
    }

    private sealed class BlockingPrompt(Func<Task<PasswordAnswer>> answer) : IPasswordPrompt
    {
        public Task<PasswordAnswer> AskAsync(PasswordQuestion question, CancellationToken ct) => answer();
    }
}
