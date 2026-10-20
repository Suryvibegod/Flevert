using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// <c>Protect with password</c>: writes <c>&lt;name&gt; (protected).pdf</c>, encrypted with AES-256, opening only with the
/// password the user chose; the owner password is the same, and nothing is restricted (docs/specs/pdf.md, "Mật khẩu").
/// </summary>
public sealed class PdfProtect : IPasswordOperation
{
    public const string AlreadyEncryptedMessage =
        "This PDF is already encrypted: its author limited what may be done with it, and Flevert leaves that as it is.";

    private readonly Qpdf _qpdf;
    private readonly BatchPasswords _passwords;

    /// <param name="prompt">Asked for the password; without one, every file fails with
    /// <see cref="PasswordPrompts.NoWindowMessage"/>, as flevert.exe without windows does.</param>
    public PdfProtect(Qpdf qpdf, IPasswordPrompt? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
        _passwords = new BatchPasswords(prompt ?? PasswordPrompts.NoWindow);
    }

    public string Id => "pdf.protect";

    public string MenuPath => "Protect with password";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // One qpdf per file, like Rotate (PdfRotate.MaxParallel); the files wait for the one question of the batch.
    public int MaxParallel => 4;

    public IPasswordOperation WithPrompt(IPasswordPrompt prompt) => new PdfProtect(_qpdf, prompt);

    public async Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        // Checked before asking, so nobody types a password for a PDF that then fails. A PDF that opens without a
        // password may still be encrypted: its author restricted printing or copying, and protecting it again would
        // drop those restrictions.
        switch (await _qpdf.GetEncryptionAsync(inputPath, ct).ConfigureAwait(false))
        {
            case PdfEncryption.NeedsPassword:
                throw new PdfException(PdfError.PasswordRequired, PdfException.NeedsPasswordMessage);
            case PdfEncryption.OpensWithoutPassword:
                throw new PdfException(PdfError.UnsupportedEncryption, AlreadyEncryptedMessage);
        }

        using var lease = await _passwords.GetAsync(inputPath, PasswordPurpose.Protect, ct).ConfigureAwait(false)
            ?? throw new OperationSkippedException(PasswordOperation.NoPasswordMessage);
        var desired = PasswordOperation.OutputBeside(inputPath, " (protected)");
        return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _qpdf.EncryptAsync(inputPath, temp, lease.Password, token), ct)
            .ConfigureAwait(false);
    }

    public void Dispose() => _passwords.Dispose();
}

/// <summary>
/// <c>Remove password</c>: writes <c>&lt;name&gt; (unlocked).pdf</c>, a copy without encryption, given the password that
/// opens the PDF. A wrong password fails that file only.
/// </summary>
public sealed class PdfUnlock : IPasswordOperation
{
    private readonly Qpdf _qpdf;
    private readonly BatchPasswords _passwords;

    /// <param name="prompt">As for <see cref="PdfProtect"/>.</param>
    public PdfUnlock(Qpdf qpdf, IPasswordPrompt? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(qpdf);
        _qpdf = qpdf;
        _passwords = new BatchPasswords(prompt ?? PasswordPrompts.NoWindow);
    }

    public string Id => "pdf.unlock";

    public string MenuPath => "Remove password";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public int MaxParallel => 4;

    public IPasswordOperation WithPrompt(IPasswordPrompt prompt) => new PdfUnlock(_qpdf, prompt);

    public async Task<string> RunAsync(string inputPath, CancellationToken ct)
    {
        // Nothing to remove is not a failure; a PDF that opens without a password keeps the restrictions its author set
        // (removing a password the user does not know is out of scope).
        switch (await _qpdf.GetEncryptionAsync(inputPath, ct).ConfigureAwait(false))
        {
            case PdfEncryption.None:
                throw new OperationSkippedException("This PDF has no password.");
            case PdfEncryption.OpensWithoutPassword:
                throw new OperationSkippedException("This PDF opens without a password.");
        }

        using var lease = await _passwords.GetAsync(inputPath, PasswordPurpose.Unlock, ct).ConfigureAwait(false)
            ?? throw new OperationSkippedException(PasswordOperation.NoPasswordMessage);
        var desired = PasswordOperation.OutputBeside(inputPath, " (unlocked)");
        return await OutputPath.WriteAtomicallyAsync(desired, (temp, token) => _qpdf.DecryptAsync(inputPath, temp, lease.Password, token), ct)
            .ConfigureAwait(false);
    }

    public void Dispose() => _passwords.Dispose();
}

internal static class PasswordOperation
{
    /// <summary>The user chose Cancel: the file is skipped, not failed.</summary>
    public const string NoPasswordMessage = "No password was given.";

    public static string OutputBeside(string inputPath, string suffix) =>
        Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + suffix + ".pdf");
}
