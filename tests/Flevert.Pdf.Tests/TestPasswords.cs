using System.Text;
using Flevert.Core;

namespace Flevert.Pdf.Tests;

/// <summary>A prompt that answers from a function and records every question.</summary>
internal sealed class FakePrompt(Func<PasswordQuestion, PasswordAnswer> answer) : IPasswordPrompt
{
    private readonly List<PasswordQuestion> _questions = [];

    public FakePrompt(string password, int files = 1)
        : this(_ => new PasswordAnswer(TestPasswords.Make(password), files))
    {
    }

    public IReadOnlyList<PasswordQuestion> Questions
    {
        get
        {
            lock (_questions)
                return [.. _questions];
        }
    }

    public Task<PasswordAnswer> AskAsync(PasswordQuestion question, CancellationToken ct)
    {
        lock (_questions)
            _questions.Add(question);
        return Task.FromResult(answer(question));
    }

    /// <summary>Answers Cancel.</summary>
    public static FakePrompt Cancelling(int files = 1) => new(_ => new PasswordAnswer(null, files));
}

internal static class TestPasswords
{
    public static PdfPassword Make(string password) => new(password.ToCharArray());

    /// <summary>
    /// Runs qpdf with every argument on standard input (<c>@-</c>, UTF-8, one per line), as Flevert does, so a password
    /// reaches it byte for byte whatever it holds.
    /// </summary>
    public static async Task<ExternalToolResult> QpdfAsync(params string[] lines)
    {
        var input = new UTF8Encoding(false).GetBytes(string.Concat(lines.Select(line => line + "\n")));
        return await ExternalTool.RunAsync(Qpdf.NextToApp.ExePath, ["@-"], TimeSpan.FromMinutes(2), input);
    }

    /// <summary>qpdf --requires-password: 0 when the password given (if any) does not open the file, 2 when it is not
    /// encrypted, 3 when it opens.</summary>
    public static async Task<int> RequiresPasswordAsync(string path, string? password = null)
    {
        string[] lines = password is null ? ["--requires-password", path] : ["--requires-password", "--password=" + password, path];
        return (await QpdfAsync(lines)).ExitCode;
    }

    /// <summary>qpdf --decrypt with the password, into <paramref name="output"/>.</summary>
    public static async Task DecryptAsync(string input, string password, string output)
    {
        var run = await QpdfAsync("--decrypt", "--password=" + password, input, output);
        Assert.True(run.ExitCode == 0, run.StandardError);
    }

    /// <summary>qpdf --encrypt with AES-256 and these passwords, into <paramref name="output"/>.</summary>
    public static async Task EncryptAsync(string input, string output, string user, string owner, params string[] restrictions)
    {
        var run = await QpdfAsync(["--encrypt", "--user-password=" + user, "--owner-password=" + owner, "--bits=256",
            .. restrictions, "--", input, output]);
        Assert.True(run.ExitCode == 0, run.StandardError);
    }

    /// <summary>
    /// The page count pdfium reads with <paramref name="password"/> given as its UTF-8 bytes (what AES-256 passwords are),
    /// or minus pdfium's error (4: password).
    /// </summary>
    public static unsafe int PdfiumPageCount(string path, string password)
    {
        var bytes = new UTF8Encoding(false).GetBytes(password + "\0");
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var source = new FileSource(file, RandomAccess.GetLength(file));
        try
        {
            lock (Pdfium.Gate)
            {
                Pdfium.EnsureInitialized();
                fixed (byte* p = bytes)
                {
                    var document = PdfiumNative.FPDF_LoadCustomDocument(source.Access, p);
                    if (document == 0)
                        return -(int)PdfiumNative.FPDF_GetLastError().Value;
                    var pages = PdfiumNative.FPDF_GetPageCount(document);
                    PdfiumNative.FPDF_CloseDocument(document);
                    return pages;
                }
            }
        }
        finally
        {
            file.Dispose();
        }
    }

    /// <summary>Every page of both PDFs renders to the same pixels (a low DPI is enough to see any change).</summary>
    public static void AssertSamePages(string expected, string actual)
    {
        using var before = PdfDocument.Open(expected);
        using var after = PdfDocument.Open(actual);
        Assert.Equal(before.PageCount, after.PageCount);
        for (var page = 0; page < before.PageCount; page++)
            Assert.True(before.Render(page, 30).Bgra.Span.SequenceEqual(after.Render(page, 30).Bgra.Span), $"page {page + 1} differs");
    }
}
