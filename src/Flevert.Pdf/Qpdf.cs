using System.Globalization;
using System.Text;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// qpdf.exe (ADR-0008), run as a separate process through <see cref="ExternalTool"/>: a crash or a hang in qpdf on an
/// untrusted PDF cannot take flevert.exe down, and qpdf ends with flevert.exe. Its exit codes and messages become
/// exceptions the user can act on.
/// </summary>
public sealed class Qpdf
{
    public const string FileName = "qpdf.exe";

    // qpdf's documented exit codes: 0 success, 2 error, 3 success with warnings.
    private const int ExitError = 2;
    private const int ExitWarnings = 3;
    // --requires-password: 0 when a password is needed to open the file.
    private const int ExitPasswordRequired = 0;

    private static readonly TimeSpan BaseTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);
    private const int MaxDetailChars = 2_000;

    // qpdf reads @- as UTF-8. NTFS names may hold a lone UTF-16 surrogate, which UTF-8 cannot carry: the default
    // encoder would put U+FFFD in its place, and qpdf would open another file of that name (one that may exist).
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal const string UnsupportedFileNameMessage =
        "The name of this PDF or of a folder it is in has a character Flevert cannot pass to its PDF tool: rename it and try again.";

    /// <param name="exePath">Absolute path of qpdf.exe; checked when qpdf is run, not here.</param>
    public Qpdf(string exePath) => ExePath = exePath;

    /// <summary>
    /// qpdf.exe in the folder of the running program: the install folder for flevert.exe (which setup checks only
    /// the user and administrators can change), or the build output, where the Flevert.Pdf project copies it.
    /// </summary>
    public static Qpdf NextToApp { get; } = new(Path.Combine(AppContext.BaseDirectory, FileName));

    public string ExePath { get; }

    /// <summary>Inputs larger than this are refused before qpdf reads them (docs/specs/pdf.md: 2 GB).</summary>
    internal long MaxInputBytes { get; init; } = PdfDocument.MaxFileBytes;

    /// <summary>
    /// Two minutes, plus a second per megabyte of input. qpdf rewrites tens of megabytes a second (a 30 MB PDF of 300 000
    /// pages took 8 s), so this only stops a run that is stuck, even for the largest PDF accepted (about 36 minutes).
    /// </summary>
    public static TimeSpan TimeoutFor(long inputBytes) => BaseTimeout + TimeSpan.FromSeconds(inputBytes / (1024 * 1024));

    /// <summary>
    /// Runs <c>qpdf &lt;options&gt; &lt;input&gt; &lt;output&gt;</c> and returns once qpdf has exited. qpdf's warnings
    /// (exit code 3) count as success: qpdf repaired something in the input and wrote a valid output.
    /// </summary>
    /// <remarks>
    /// The two paths reach qpdf through standard input (<c>@-</c>, one argument per line), not its command line: qpdf's
    /// C runtime expands wildcards in command-line arguments, which turns the <c>?</c> of a <c>\\?\</c> path into other
    /// file names. Lines read from <c>@-</c> are taken as they are, and both are always given in the <c>\\?\</c> form
    /// (<see cref="ToExtendedPath"/>), the only one qpdf opens past 260 characters.
    /// </remarks>
    /// <param name="inputPath">Fully qualified, so qpdf can never read it as an option (<c>-…</c>) or an option file (<c>@…</c>).</param>
    /// <param name="outputPath">Fully qualified, like <paramref name="inputPath"/>; usually the temporary path from <see cref="OutputPath.WriteAtomicallyAsync"/>.</param>
    /// <exception cref="PdfException">
    /// The input needs a password, is not a PDF or is damaged beyond repair, or is too large; or a path has a character
    /// qpdf cannot be given (<see cref="PdfError.UnsupportedFileName"/>).
    /// </exception>
    /// <exception cref="IOException">qpdf could not write <paramref name="outputPath"/>.</exception>
    /// <exception cref="FileNotFoundException">qpdf.exe is missing.</exception>
    public async Task TransformAsync(string inputPath, string outputPath, IEnumerable<string> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        string[] commandLine = [.. options];
        // The options stay on the command line, where qpdf's C runtime would expand them as wildcards.
        if (commandLine.FirstOrDefault(option => option.AsSpan().ContainsAny('*', '?')) is { } wild)
            throw new ArgumentException($"qpdf would expand the wildcard in the option {wild}.", nameof(options));
        // qpdf has no end-of-options marker ("--" is not one): a relative path such as "-x.pdf" would be an option.
        RequireArgumentLine(inputPath, nameof(inputPath));
        RequireArgumentLine(outputPath, nameof(outputPath));
        RequireExe();

        var length = new FileInfo(inputPath).Length;
        if (length > MaxInputBytes)
            throw PdfDocument.TooLargeError(MaxInputBytes);

        var run = await RunWithPathsAsync(commandLine, [inputPath, outputPath], TimeoutFor(length), ct).ConfigureAwait(false);
        if (run.ExitCode is 0 or ExitWarnings)
        {
            if (!File.Exists(outputPath))
                throw new QpdfException(run.ExitCode, $"it reported success but wrote no output. {Detail(run)}");
            return;
        }

        var detail = Detail(run);
        if (run.ExitCode != ExitError)
            throw new QpdfException(run.ExitCode, detail);
        if (await RequiresPasswordAsync(inputPath, ct).ConfigureAwait(false))
            throw new PdfException(PdfError.PasswordRequired, PdfException.NeedsPasswordMessage);
        // qpdf names the file it failed on, as it was given; the output's name is a fresh temporary one no input contains.
        if (run.StandardError.Contains(ToExtendedPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"qpdf could not write the output: {detail}", new QpdfException(run.ExitCode, detail));
        throw new PdfException(PdfError.NotPdf, PdfDocument.NotPdfMessage, new QpdfException(run.ExitCode, detail));
    }

    // Asked only after a failure, so a PDF that opens costs no second process. qpdf documents these exit codes, while
    // its error text ("invalid password") may change between versions.
    private async Task<bool> RequiresPasswordAsync(string inputPath, CancellationToken ct)
    {
        var probe = await RunWithPathsAsync(["--requires-password"], [inputPath], ProbeTimeout, ct).ConfigureAwait(false);
        return probe.ExitCode == ExitPasswordRequired;
    }

    private Task<ExternalToolResult> RunWithPathsAsync(string[] options, string[] paths, TimeSpan timeout, CancellationToken ct)
    {
        byte[] lines;
        try
        {
            lines = StrictUtf8.GetBytes(string.Concat(paths.Select(path => ToExtendedPath(path) + "\n")));
        }
        catch (EncoderFallbackException e)
        {
            throw new PdfException(PdfError.UnsupportedFileName, UnsupportedFileNameMessage, e);
        }

        return ExternalTool.RunAsync(ExePath, [.. options, "@-"], timeout, lines, cancellationToken: ct);
    }

    /// <summary>
    /// The <c>\\?\</c> form of a fully qualified path, which qpdf opens at any length; without it qpdf cannot open a path
    /// past 260 characters (nor in the <c>\\.\</c> form; checked with 12.4.2), and the runner shortens an input under 260
    /// characters while the temporary output beside it is 51 characters longer. The prefix turns off Windows'
    /// normalisation, so the path is normalised first, as .NET does when it opens the same path.
    /// </summary>
    internal static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal))
            return @"\\?\" + full[4..];
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private void RequireExe()
    {
        if (!File.Exists(ExePath))
        {
            throw new FileNotFoundException(
                $"{FileName} is missing from {Path.GetDirectoryName(ExePath)}. Reinstall Flevert (in a source build, run scripts\\fetch-deps.ps1).",
                ExePath);
        }
    }

    private static void RequireArgumentLine(string path, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(path, name);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"The path must be fully qualified: {path}", name);
        // One argument per line of @-. Windows names cannot hold line breaks, but a path given here must not either.
        if (path.AsSpan().ContainsAny('\r', '\n'))
            throw new ArgumentException("The path must not contain a line break.", name);
    }

    // qpdf's error is the last line of stderr. ExternalTool keeps the first megabyte, so the end of what it kept is
    // the error itself, or, after a megabyte of warnings, the last warnings kept.
    private static string Detail(ExternalToolResult run)
    {
        var text = run.StandardError.Trim();
        if (text.Length == 0)
            text = string.Create(CultureInfo.InvariantCulture, $"no message, exit code {run.ExitCode}");
        return text.Length <= MaxDetailChars ? text : "…" + text[^MaxDetailChars..];
    }
}

/// <summary>qpdf failed in a way Flevert has no better words for; the message holds qpdf's own.</summary>
public sealed class QpdfException : Exception
{
    public QpdfException() { }

    public QpdfException(string message) : base(message) { }

    public QpdfException(string message, Exception innerException) : base(message, innerException) { }

    public QpdfException(int exitCode, string detail)
        : base(string.Create(CultureInfo.InvariantCulture, $"qpdf failed (exit code {exitCode}): {detail}")) => ExitCode = exitCode;

    public int ExitCode { get; }
}
