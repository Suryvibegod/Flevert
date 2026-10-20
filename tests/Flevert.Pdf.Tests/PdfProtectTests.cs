using System.Security.Cryptography;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPasswords;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>Protect with password and Remove password, with the real qpdf and a prompt that answers for the user.</summary>
public sealed class PdfProtectTests : IDisposable
{
    private const string FixturePassword = "flevert-test"; // fixtures/README.md
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string CopyFixture(string fixture, string name)
    {
        var path = _dir.Combine(name);
        File.Copy(Fixture(fixture), path);
        return path;
    }

    private async Task<string> OwnerOnly(string name)
    {
        var path = _dir.Combine(name);
        await EncryptAsync(Fixture("three-colours.pdf"), path, user: "", owner: "author", "--print=none");
        return path;
    }

    [Fact]
    public void The_operations_are_named_and_placed_as_in_the_spec()
    {
        var protect = PdfOperations.All.OfType<PdfProtect>().Single();
        var unlock = PdfOperations.All.OfType<PdfUnlock>().Single();

        Assert.Equal(("pdf.protect", "Protect with password"), (protect.Id, protect.MenuPath));
        Assert.Equal(("pdf.unlock", "Remove password"), (unlock.Id, unlock.MenuPath));
        foreach (IPasswordOperation op in new IPasswordOperation[] { protect, unlock })
        {
            Assert.Equal([".pdf"], op.Extensions);
            Assert.Equal(4, op.MaxParallel);
            using var asking = op.WithPrompt(new FakePrompt("pw"));
            Assert.NotSame(op, asking);
            Assert.Equal(op.GetType(), asking.GetType());
        }
    }

    [Fact]
    public async Task Protect_writes_an_aes_256_copy_that_opens_only_with_the_password()
    {
        var input = CopyFixture("three-colours.pdf", "Hóa đơn.pdf");
        var before = SHA256.HashData(File.ReadAllBytes(input));
        var prompt = new FakePrompt("pw ✓");
        using var protect = new PdfProtect(Qpdf.NextToApp, prompt);

        var output = await protect.RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Hóa đơn (protected).pdf"), output);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(input)));
        Assert.Equal([new PasswordQuestion(input, PasswordPurpose.Protect, 0)], prompt.Questions);
        Assert.Equal(0, await RequiresPasswordAsync(output));
        Assert.Equal(0, await RequiresPasswordAsync(output, "pw"));
        Assert.Equal(3, await RequiresPasswordAsync(output, "pw ✓"));
        var encryption = (await QpdfAsync("--show-encryption", "--password=pw ✓", output)).StandardOutput;
        Assert.Contains("R = 6", encryption, StringComparison.Ordinal);
        Assert.Contains("stream encryption method: AESv3", encryption, StringComparison.Ordinal);
        Assert.Contains("string encryption method: AESv3", encryption, StringComparison.Ordinal);
        Assert.Contains("file encryption method: AESv3", encryption, StringComparison.Ordinal);
        // The owner password is the user's: whoever opens the copy may do anything with it.
        Assert.Contains("Supplied password is owner password", encryption, StringComparison.Ordinal);
        Assert.Contains("Supplied password is user password", encryption, StringComparison.Ordinal);
        Assert.DoesNotContain("not allowed", encryption, StringComparison.Ordinal);

        Assert.Equal(PdfError.PasswordRequired, Assert.Throws<PdfException>(() => PdfDocument.Open(output)).Error);
        Assert.Equal(3, PdfiumPageCount(output, "pw ✓"));
        Assert.Equal(-4, PdfiumPageCount(output, "pw"));
        var opened = _dir.Combine("opened.pdf");
        await DecryptAsync(output, "pw ✓", opened);
        AssertSamePages(input, opened);
    }

    public static TheoryData<string> Passwords() =>
    [
        "two words",
        "  spaces before and after  ",
        " ",
        "Mật khẩu tiếng Việt có dấu",
        "emoji 🔒🗝️ and 👨‍👩‍👧",
        "\"double\" 'single' `back`",
        "@starts-with-an-at",
        "-starts-with-a-dash",
        "--password=inside",
        @"back\slash\\ and %d %s",
        "a=b=c",
        "tab\there",
        new string('x', 127),
        string.Concat(Enumerable.Repeat("🔒", 31)),
    ];

    [Theory]
    [MemberData(nameof(Passwords))]
    public async Task A_password_reaches_qpdf_exactly_as_typed_and_unlocks_again(string password)
    {
        var input = CopyFixture("three-colours.pdf", "scan.pdf");
        using var protect = new PdfProtect(Qpdf.NextToApp, new FakePrompt(password));
        using var unlock = new PdfUnlock(Qpdf.NextToApp, new FakePrompt(password));

        var protectedPdf = await protect.RunAsync(input, CancellationToken.None);
        var unlocked = await unlock.RunAsync(protectedPdf, CancellationToken.None);

        Assert.Equal(3, await RequiresPasswordAsync(protectedPdf, password));
        Assert.Equal(3, PdfiumPageCount(protectedPdf, password));
        if (password.Trim() is { Length: > 0 } trimmed && trimmed != password)
            Assert.Equal(0, await RequiresPasswordAsync(protectedPdf, trimmed));
        Assert.Equal(_dir.Combine("scan (protected) (unlocked).pdf"), unlocked);
        Assert.Equal(2, await RequiresPasswordAsync(unlocked));
        AssertSamePages(input, unlocked);
    }

    [Fact]
    public async Task Remove_password_with_the_right_password_writes_a_copy_without_encryption()
    {
        var input = CopyFixture("password-protected.pdf", "Hóa đơn.pdf");
        var prompt = new FakePrompt(FixturePassword);
        using var unlock = new PdfUnlock(Qpdf.NextToApp, prompt);

        var output = await unlock.RunAsync(input, CancellationToken.None);

        Assert.Equal(_dir.Combine("Hóa đơn (unlocked).pdf"), output);
        Assert.Equal([new PasswordQuestion(input, PasswordPurpose.Unlock, 0)], prompt.Questions);
        Assert.Equal(2, await RequiresPasswordAsync(output));
        Assert.Contains("File is not encrypted", (await QpdfAsync("--show-encryption", output)).StandardOutput, StringComparison.Ordinal);
        AssertSamePages(Fixture("three-colours.pdf"), output);
    }

    [Fact]
    public async Task A_wrong_password_fails_that_file_with_a_clear_message_and_writes_nothing()
    {
        var input = CopyFixture("password-protected.pdf", "scan.pdf");
        using var unlock = new PdfUnlock(Qpdf.NextToApp, new FakePrompt("not-the-password"));

        var error = await Assert.ThrowsAsync<PdfException>(() => unlock.RunAsync(input, CancellationToken.None));

        Assert.Equal(PdfError.WrongPassword, error.Error);
        Assert.Equal(PdfException.WrongPasswordMessage, error.Message);
        Assert.DoesNotContain("not-the-password", error.ToString(), StringComparison.Ordinal);
        Assert.Equal([input], Directory.GetFileSystemEntries(_dir.Path));
    }

    [Fact]
    public async Task One_password_for_the_whole_batch_unlocks_the_files_it_opens_and_fails_only_the_other()
    {
        var a = CopyFixture("password-protected.pdf", "a.pdf");
        var b = CopyFixture("password-protected.pdf", "b.pdf");
        var other = _dir.Combine("c.pdf");
        await EncryptAsync(Fixture("three-colours.pdf"), other, "another", "another");
        var prompt = new FakePrompt(FixturePassword, files: 3);
        using var unlock = new PdfUnlock(Qpdf.NextToApp, prompt);
        var logs = new TempDir();
        try
        {
            var outcome = await new Runner(new RunLog(logs.Path)).RunAsync(unlock, [a, b, other], null, CancellationToken.None, maxParallel: 4);

            Assert.Single(prompt.Questions);
            Assert.Equal(3, outcome.Total);
            Assert.True(outcome.Results.Single(r => r.Input == a).Succeeded);
            Assert.True(outcome.Results.Single(r => r.Input == b).Succeeded);
            var failed = outcome.Results.Single(r => r.Input == other);
            Assert.True(failed.Failed);
            Assert.Contains(PdfException.WrongPasswordMessage, failed.Error, StringComparison.Ordinal);
            Assert.True(File.Exists(_dir.Combine("a (unlocked).pdf")));
            Assert.True(File.Exists(_dir.Combine("b (unlocked).pdf")));
            Assert.False(File.Exists(_dir.Combine("c (unlocked).pdf")));
        }
        finally
        {
            logs.Dispose();
        }
    }

    [Fact]
    public async Task Remove_password_skips_a_pdf_without_one_and_asks_nothing()
    {
        var plain = CopyFixture("three-colours.pdf", "plain.pdf");
        var restricted = await OwnerOnly("restricted.pdf");
        var prompt = new FakePrompt("pw");
        using var unlock = new PdfUnlock(Qpdf.NextToApp, prompt);

        var none = await Assert.ThrowsAsync<OperationSkippedException>(() => unlock.RunAsync(plain, CancellationToken.None));
        var opens = await Assert.ThrowsAsync<OperationSkippedException>(() => unlock.RunAsync(restricted, CancellationToken.None));

        Assert.Equal("This PDF has no password.", none.Message);
        Assert.Equal("This PDF opens without a password.", opens.Message);
        Assert.Empty(prompt.Questions);
        Assert.Equal(2, Directory.GetFileSystemEntries(_dir.Path).Length);
    }

    [Fact]
    public async Task Protect_refuses_an_encrypted_pdf_and_asks_nothing()
    {
        var locked = CopyFixture("password-protected.pdf", "locked.pdf");
        var restricted = await OwnerOnly("restricted.pdf");
        var prompt = new FakePrompt("pw");
        using var protect = new PdfProtect(Qpdf.NextToApp, prompt);

        var needs = await Assert.ThrowsAsync<PdfException>(() => protect.RunAsync(locked, CancellationToken.None));
        var author = await Assert.ThrowsAsync<PdfException>(() => protect.RunAsync(restricted, CancellationToken.None));

        Assert.Equal((PdfError.PasswordRequired, PdfException.NeedsPasswordMessage), (needs.Error, needs.Message));
        // Protecting it again would drop the restrictions its author set; Flevert does not remove them.
        Assert.Equal(PdfError.UnsupportedEncryption, author.Error);
        Assert.Equal(PdfProtect.AlreadyEncryptedMessage, author.Message);
        Assert.Empty(prompt.Questions);
        Assert.Equal(2, Directory.GetFileSystemEntries(_dir.Path).Length);
    }

    [Fact]
    public async Task A_file_that_is_not_a_pdf_fails_before_anyone_is_asked()
    {
        var input = _dir.File("notes.pdf", "not a PDF at all");
        var prompt = new FakePrompt("pw");
        using var protect = new PdfProtect(Qpdf.NextToApp, prompt);
        using var unlock = new PdfUnlock(Qpdf.NextToApp, prompt);

        Assert.Equal(PdfError.NotPdf, (await Assert.ThrowsAsync<PdfException>(() => protect.RunAsync(input, CancellationToken.None))).Error);
        Assert.Equal(PdfError.NotPdf, (await Assert.ThrowsAsync<PdfException>(() => unlock.RunAsync(input, CancellationToken.None))).Error);
        Assert.Empty(prompt.Questions);
    }

    [Fact]
    public async Task Cancel_skips_the_file_and_writes_nothing()
    {
        var plain = CopyFixture("three-colours.pdf", "plain.pdf");
        var locked = CopyFixture("password-protected.pdf", "locked.pdf");
        using var protect = new PdfProtect(Qpdf.NextToApp, FakePrompt.Cancelling());
        using var unlock = new PdfUnlock(Qpdf.NextToApp, FakePrompt.Cancelling());

        var skipped = await Assert.ThrowsAsync<OperationSkippedException>(() => protect.RunAsync(plain, CancellationToken.None));
        await Assert.ThrowsAsync<OperationSkippedException>(() => unlock.RunAsync(locked, CancellationToken.None));

        Assert.Equal("No password was given.", skipped.Message);
        Assert.Equal(2, Directory.GetFileSystemEntries(_dir.Path).Length);
    }

    [Fact]
    public async Task The_operations_in_the_catalog_have_no_window_to_ask_in_and_fail_clearly()
    {
        var plain = CopyFixture("three-colours.pdf", "plain.pdf");
        var locked = CopyFixture("password-protected.pdf", "locked.pdf");

        var protect = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PdfOperations.All.OfType<PdfProtect>().Single().RunAsync(plain, CancellationToken.None));
        var unlock = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PdfOperations.All.OfType<PdfUnlock>().Single().RunAsync(locked, CancellationToken.None));

        Assert.Equal(PasswordPrompts.NoWindowMessage, protect.Message);
        Assert.Equal(PasswordPrompts.NoWindowMessage, unlock.Message);
        Assert.Equal(2, Directory.GetFileSystemEntries(_dir.Path).Length);
    }

    [Fact]
    public async Task A_password_qpdf_repeats_in_its_error_goes_no_further()
    {
        const string marker = "Leak-marker-Ẩn-🔑";
        var input = CopyFixture("three-colours.pdf", "plain.pdf");
        var output = _dir.Combine("out.pdf");
        // With --bits=256 first, qpdf refuses the passwords after it and repeats them: "unrecognized argument --user-password=…".
        var direct = await QpdfAsync("--encrypt", "--bits=256", "--user-password=" + marker, "--owner-password=" + marker, "--", input, output);
        Assert.Contains(marker, direct.StandardError, StringComparison.Ordinal);
        var qpdf = new Qpdf(Qpdf.NextToApp.ExePath) { EncryptOptionsBeforePasswords = ["--bits=256"] };
        using var password = Make(marker);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => qpdf.EncryptAsync(input, output, password, CancellationToken.None));

        Assert.DoesNotContain(marker, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("could hold the password", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }
}
