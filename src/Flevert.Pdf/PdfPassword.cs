using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// A password the user typed, held as characters Flevert can wipe: it never becomes a <see cref="string"/> in Flevert's
/// code, and <see cref="Dispose"/> overwrites it with zeros (docs/specs/pdf.md, "Mật khẩu").
/// </summary>
public sealed class PdfPassword : IDisposable
{
    /// <summary>
    /// The most a PDF password holds in UTF-8 (AES-256, revision 6, truncates to 127 bytes). qpdf 12.4.2 writes a PDF
    /// with a longer one that then opens with neither the password nor its first 127 bytes, so a longer one is refused.
    /// </summary>
    public const int MaxUtf8Bytes = 127;

    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly char[] _chars;
    private bool _disposed;

    /// <param name="chars">Taken over, not copied: zeroed by <see cref="Dispose"/>.</param>
    public PdfPassword(char[] chars)
    {
        ArgumentNullException.ThrowIfNull(chars);
        _chars = chars;
    }

    public ReadOnlySpan<char> Chars
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _chars;
        }
    }

    /// <summary>Overwrites the characters with zeros. Safe to call more than once.</summary>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_chars.AsSpan()));
        _disposed = true;
    }
}

/// <summary>What the password is for: the dialog asks for it twice to protect a PDF, once to unlock one.</summary>
public enum PasswordPurpose
{
    Protect,
    Unlock,
}

/// <summary>The checks the password dialog makes before it lets OK through.</summary>
public static class PasswordRules
{
    public const string EmptyMessage = "Type a password.";
    public const string MismatchMessage = "The two passwords are not the same.";
    public const string TooLongMessage =
        "This password is too long: a PDF password holds at most 127 bytes (127 plain letters; an accented letter takes 2, an emoji 4).";
    public const string UnsupportedCharacterMessage = "This password has a character a PDF password cannot hold.";

    /// <summary>Why the password cannot be used, or <c>null</c> when it can.</summary>
    /// <param name="again">The second entry; only compared when protecting.</param>
    public static string? Problem(ReadOnlySpan<char> password, ReadOnlySpan<char> again, PasswordPurpose purpose)
    {
        if (password.IsEmpty)
            return EmptyMessage;
        // qpdf reads one argument per line of @-, as a C string; a lone surrogate has no UTF-8 form (and the default
        // encoder would put U+FFFD in its place, another password).
        if (password.ContainsAny('\r', '\n', '\0'))
            return UnsupportedCharacterMessage;
        int bytes;
        try
        {
            bytes = PdfPassword.StrictUtf8.GetByteCount(password);
        }
        catch (EncoderFallbackException)
        {
            return UnsupportedCharacterMessage;
        }

        if (bytes > PdfPassword.MaxUtf8Bytes)
            return TooLongMessage;
        // Exactly, spaces included: a password is what was typed.
        return purpose == PasswordPurpose.Protect && !password.SequenceEqual(again) ? MismatchMessage : null;
    }
}

/// <param name="InputPath">The PDF the password is for.</param>
/// <param name="AnsweredInProgress">Files of this batch that already have their password and are still being worked on;
/// the dialog leaves them out of the count it offers the password for ("Use this password for all 4 files").</param>
public sealed record PasswordQuestion(string InputPath, PasswordPurpose Purpose, int AnsweredInProgress);

/// <param name="Password">The password, or <c>null</c> when the user chose Cancel.</param>
/// <param name="Files">How many files of the batch the answer is for, the one asked about included: 1, or N when the
/// user kept "Use this password for all N files".</param>
public sealed record PasswordAnswer(PdfPassword? Password, int Files);

/// <summary>Asks the user for a password: the dialog of flevert.exe, or a fake in tests.</summary>
public interface IPasswordPrompt
{
    /// <exception cref="InvalidOperationException">There is no way to ask (flevert.exe runs without windows).</exception>
    Task<PasswordAnswer> AskAsync(PasswordQuestion question, CancellationToken ct);
}

/// <summary>The prompts that need no window.</summary>
public static class PasswordPrompts
{
    public const string NoWindowMessage =
        "Flevert asks for a PDF password only in a window, and it runs without windows here (FLEVERT_HEADLESS), so this PDF was left as it is.";

    /// <summary>Refuses every question: what protect and unlock use until flevert.exe gives them its dialog.</summary>
    public static IPasswordPrompt NoWindow { get; } = new NoWindowPrompt();

    private sealed class NoWindowPrompt : IPasswordPrompt
    {
        // Fails at once: nothing may wait for an answer no one can give (docs/specs/pdf.md, "Mật khẩu").
        public Task<PasswordAnswer> AskAsync(PasswordQuestion question, CancellationToken ct) =>
            Task.FromException<PasswordAnswer>(new InvalidOperationException(NoWindowMessage));
    }
}

/// <summary>
/// The passwords of one batch: asks one question at a time, and an answer given for N files serves the next N − 1 files
/// without asking again. Each file holds its password through a <see cref="PasswordLease"/>; a password for one file is
/// wiped when that file is done, a shared one when the last file it was given for is done, or when the batch ends.
/// </summary>
/// <remarks>
/// The count is the user's: "Use this password for all 5 files" serves 5 files, the one asked about and the next 4 that
/// ask, so a file that joins the batch later (another selection while this one runs) is asked about again. Accepted: a
/// file counted among the 5 that then fails before it asks (already protected, not a PDF) leaves its turn to a file
/// that joins later.
/// </remarks>
public sealed class BatchPasswords : IDisposable
{
    private readonly IPasswordPrompt _prompt;
    // Never disposed: it has no wait handle, and a file still waiting when the batch ends must not throw from Release.
    private readonly SemaphoreSlim _asking = new(1, 1);
    private readonly Lock _gate = new();
    private readonly List<PdfPassword> _held = [];
    private Shared? _shared;
    private int _inUse;
    private bool _disposed;

    public BatchPasswords(IPasswordPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        _prompt = prompt;
    }

    /// <summary>The password for <paramref name="inputPath"/>, or <c>null</c> when the user chose Cancel for it.</summary>
    /// <remarks>One question at a time: files that ask meanwhile wait, and may be served by its answer.</remarks>
    public async Task<PasswordLease?> GetAsync(string inputPath, PasswordPurpose purpose, CancellationToken ct)
    {
        await _asking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int inUse;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_shared is { Remaining: > 0 } shared)
                {
                    shared.Remaining--;
                    return Lease(shared);
                }

                inUse = _inUse;
            }

            var answer = await _prompt.AskAsync(new PasswordQuestion(inputPath, purpose, inUse), ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed)
                {
                    answer.Password?.Dispose();
                    throw new ObjectDisposedException(nameof(BatchPasswords));
                }

                if (answer.Password is not null)
                    _held.Add(answer.Password);
                _shared = new Shared(answer.Password, Math.Max(answer.Files, 1) - 1);
                return Lease(_shared);
            }
        }
        finally
        {
            _asking.Release();
        }
    }

    // Under _gate. Cancel gives no lease; the last lease of an answer with no files left to serve wipes its password.
    private PasswordLease? Lease(Shared shared)
    {
        if (shared.Password is not { } password)
            return null;
        shared.Holders++;
        _inUse++;
        return new PasswordLease(password, () =>
        {
            lock (_gate)
            {
                _inUse--;
                if (--shared.Holders == 0 && shared.Remaining == 0)
                {
                    password.Dispose();
                    _held.Remove(password);
                }
            }
        });
    }

    /// <summary>Wipes every password still held. Leases still out keep a wiped password.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var password in _held)
                password.Dispose();
            _held.Clear();
        }
    }

    private sealed class Shared(PdfPassword? password, int remaining)
    {
        public PdfPassword? Password { get; } = password;

        /// <summary>Files the answer may still serve without asking.</summary>
        public int Remaining { get; set; } = remaining;

        /// <summary>Files using the password now.</summary>
        public int Holders { get; set; }
    }
}

/// <summary>One file's hold on a password; dispose it once qpdf has finished with the password.</summary>
public sealed class PasswordLease : IDisposable
{
    internal PasswordLease(PdfPassword password, Action release)
    {
        Password = password;
        _release = release;
    }

    private Action? _release;

    public PdfPassword Password { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>An operation that asks for a password: flevert.exe gives it a prompt for the batch it runs.</summary>
public interface IPasswordOperation : IOperation, IDisposable
{
    /// <summary>A copy of this operation that asks <paramref name="prompt"/>, once per batch where the user agrees.
    /// Dispose it when the batch ends, which wipes the passwords it still holds.</summary>
    IPasswordOperation WithPrompt(IPasswordPrompt prompt);
}
