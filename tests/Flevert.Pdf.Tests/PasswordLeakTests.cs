using System.Collections.Concurrent;
using System.Text;
using Flevert.Core;
using static Flevert.Pdf.Tests.TestPasswords;
using static Flevert.Pdf.Tests.TestPdf;

namespace Flevert.Pdf.Tests;

/// <summary>
/// docs/specs/pdf.md, success criterion 5: a password goes nowhere but qpdf's standard input. Protect and Remove password
/// run as flevert.exe runs them (the runner, its log, qpdf through ExternalTool) with a marker for a password, and the
/// marker is looked for in every place it could end up.
/// </summary>
public sealed class PasswordLeakTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task A_password_is_in_no_log_command_line_environment_or_file()
    {
        // The ASCII part alone is searched for too, so a password written in any encoding is found.
        var tag = "FlevertLeak" + Guid.NewGuid().ToString("N")[..12];
        var password = tag + " Mật khẩu 🔑";
        var wrong = tag + " wrong";
        var started = DateTime.UtcNow.AddSeconds(-2);
        // Large enough that qpdf runs for a while (about 2 s to encrypt), long enough to be read while it does; one page,
        // since a PDF of more than 2000 is refused before anyone is asked.
        var input = _dir.Combine("big.pdf");
        MaliciousPdf.WriteManyObjects(input, 150_000);
        var locked = _dir.Combine("locked.pdf");
        File.Copy(Fixture("password-protected.pdf"), locked);

        var seen = new ConcurrentBag<ProcessPeek.Started>();
        var partials = new ConcurrentBag<string>();
        using var stop = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var qpdf in ProcessPeek.Children("qpdf"))
                    seen.Add(qpdf);
                // The temporary outputs, while qpdf writes them.
                foreach (var file in Directory.EnumerateFiles(_dir.Path).Where(f => f.Contains(".partial", StringComparison.Ordinal)))
                {
                    if (Holds(file, tag))
                        partials.Add(file);
                }

                await Task.Delay(5);
            }
        });

        var runner = new Runner(new RunLog(_logs.Path));
        BatchOutcome protecting, unlocking, failing;
        try
        {
            using (var protect = new PdfProtect(Qpdf.NextToApp, new FakePrompt(password)))
                protecting = await runner.RunAsync(protect, [input], null, CancellationToken.None);
            var protectedPdf = protecting.Results.Single().Output!;
            using (var unlock = new PdfUnlock(Qpdf.NextToApp, new FakePrompt(password)))
                unlocking = await runner.RunAsync(unlock, [protectedPdf], null, CancellationToken.None);
            using (var unlock = new PdfUnlock(Qpdf.NextToApp, new FakePrompt(wrong)))
                failing = await runner.RunAsync(unlock, [locked], null, CancellationToken.None);
            Assert.Equal(3, await RequiresPasswordAsync(protectedPdf, password));
        }
        finally
        {
            await stop.CancelAsync();
            await watcher;
        }

        Assert.True(protecting.NoFailures);
        Assert.True(unlocking.NoFailures);
        Assert.Contains(PdfException.WrongPasswordMessage, failing.Results.Single().Error, StringComparison.Ordinal);

        // qpdf was seen while encrypting and decrypting, so the checks below looked at real runs.
        Assert.Contains(seen, qpdf => qpdf.CommandLine.Contains("--encrypt", StringComparison.Ordinal));
        Assert.Contains(seen, qpdf => qpdf.CommandLine.Contains("--decrypt", StringComparison.Ordinal));
        Assert.All(seen, qpdf =>
        {
            Assert.DoesNotContain(tag, qpdf.CommandLine, StringComparison.Ordinal);
            Assert.DoesNotContain(tag, qpdf.Environment, StringComparison.Ordinal);
        });
        Assert.Empty(partials);

        var logs = Directory.GetFiles(_logs.Path, "*", SearchOption.AllDirectories);
        Assert.Contains(logs, log => File.ReadAllText(log).Contains(PdfException.WrongPasswordMessage, StringComparison.Ordinal));
        Assert.All(logs, log => Assert.False(Holds(log, tag), log));
        Assert.All(Directory.GetFiles(_dir.Path, "*", SearchOption.AllDirectories), file => Assert.False(Holds(file, tag), file));
        // Anything written to the temporary folder meanwhile, by Flevert, qpdf or OpenSSL.
        var temporary = new DirectoryInfo(Path.GetTempPath())
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 });
        // An empty entry holds nothing; on Linux it may be a FIFO or a socket, which a read would wait on forever.
        temporary = temporary.Where(file => file.LastWriteTimeUtc >= started && file.Length is > 0 and < 64L << 20
            && !file.FullName.StartsWith(_dir.Path, StringComparison.OrdinalIgnoreCase));
        Assert.All(temporary, file => Assert.False(Holds(file.FullName, tag), file.FullName));
    }

    // The tag as UTF-8 (and so ASCII or Latin-1) or UTF-16 bytes; a file that cannot be read now is skipped.
    private static bool Holds(string path, string tag)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(tag)) >= 0 || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(tag)) >= 0;
    }
}
