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

    /// <summary>
    /// How qpdf's environment differs from flevert.exe's. qpdf links OpenSSL, which loads the configuration file
    /// OPENSSL_CONF names (qpdf 12.4.2 opens it even to rotate), and a configuration can load modules and engines from
    /// the folders OPENSSL_MODULES and OPENSSL_ENGINES name: code from wherever the user's environment points. Unset,
    /// OpenSSL would use the folders it was built with, under C:\Program Files, and C: need not be the system drive. So
    /// all three name Flevert's own folder, which setup checks only the user and administrators can change, and the
    /// configuration there is empty. qpdf's own variables (QPDF_*) go too: they choose its crypto and compressor.
    /// </summary>
    internal IReadOnlyDictionary<string, string?> ToolEnvironment()
    {
        var folder = Path.GetDirectoryName(ExePath)!;
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENSSL_CONF"] = Path.Combine(folder, OpenSslConfigFileName),
            ["OPENSSL_MODULES"] = folder,
            ["OPENSSL_ENGINES"] = folder,
            ["OPENSSL_CONF_INCLUDE"] = null,
        };
        // QPDF_CRYPTO_PROVIDER, QPDF_ZOPFLI and whatever later versions read.
        foreach (var name in System.Environment.GetEnvironmentVariables().Keys.OfType<string>())
        {
            if (name.StartsWith("QPDF_", StringComparison.OrdinalIgnoreCase))
                environment[name] = null;
        }

        return environment;
    }

    /// <summary>An OpenSSL configuration with no settings, shipped beside qpdf.exe (the Flevert.Pdf project copies it).</summary>
    internal const string OpenSslConfigFileName = "openssl.cnf";

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

    private const ulong MinMemoryBytes = 1UL << 30;
    private const ulong MaxMemoryCeilingBytes = 3UL << 30;
    private const ulong BaseMemoryBytes = 256UL << 20;

    /// <summary>
    /// The memory one qpdf may commit for an input of <paramref name="inputBytes"/> (ADR-0008): 256 MB plus 2.5 times the
    /// file, at least 1 GB and at most 3 GB. Measured with 12.4.2: qpdf holds the object structure (500 000 small pages,
    /// a 50 MB file, took 830 MB) and a stream it copies about twice over (300 MB took 602 MB, 520 MB took 1 043 MB); a
    /// stream it re-encodes about once (300 MB took 303 MB). A PDF needing more is refused.
    /// </summary>
    public static ulong MaxMemoryFor(long inputBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputBytes);
        var scaled = BaseMemoryBytes + (ulong)inputBytes * 5 / 2;
        return Math.Clamp(scaled, MinMemoryBytes, MaxMemoryCeilingBytes);
    }

    /// <summary>A fixed limit instead of <see cref="MaxMemoryFor"/>, for tests.</summary>
    internal ulong? MaxMemoryBytes { get; init; }

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
    /// The input needs a password, is not a PDF or is damaged beyond repair, is too large, or needs more memory than
    /// <see cref="MaxMemoryFor"/> allows (<see cref="PdfError.NeedsTooMuchMemory"/>, whatever qpdf's exit code); or a path
    /// has a character qpdf cannot be given (<see cref="PdfError.UnsupportedFileName"/>).
    /// </exception>
    /// <exception cref="IOException">qpdf could not write <paramref name="outputPath"/>.</exception>
    /// <exception cref="FileNotFoundException">qpdf.exe is missing.</exception>
    public async Task TransformAsync(string inputPath, string outputPath, IEnumerable<string> options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var run = await RunCheckedAsync([.. options], [inputPath], [inputPath, outputPath], outputPath, ct).ConfigureAwait(false);
        RequireOutput(run, outputPath);
    }

    /// <summary>
    /// Runs <c>qpdf --empty --pages &lt;input&gt;… -- &lt;output&gt;</c>: every page of every input, in the order given,
    /// into one new PDF. The inputs and the output reach qpdf through <c>@-</c>, like the paths of
    /// <see cref="TransformAsync"/>; a page range may follow a file in <c>--pages</c>, and a <c>\\?\</c> path never reads
    /// as one.
    /// </summary>
    /// <remarks>
    /// qpdf holds every input at once, so the size limit, the memory cap (<see cref="MaxMemoryFor"/>) and the timeout go by
    /// the inputs' total size.
    /// </remarks>
    /// <param name="inputPaths">At least two, fully qualified.</param>
    /// <exception cref="BatchInputException">
    /// One input cannot be used: it needs a password, is not a PDF or is damaged, or is too large. It names that input,
    /// and its inner exception is the <see cref="PdfException"/> <see cref="TransformAsync"/> would throw for it alone.
    /// </exception>
    /// <exception cref="PdfException">
    /// The inputs together are too large or need more memory than allowed, a path has a character qpdf cannot be given,
    /// or qpdf failed on an input without naming it.
    /// </exception>
    /// <exception cref="IOException">qpdf could not write <paramref name="outputPath"/>.</exception>
    public async Task MergeAsync(IReadOnlyList<string> inputPaths, string outputPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentOutOfRangeException.ThrowIfLessThan(inputPaths.Count, 2, nameof(inputPaths));
        var run = await RunCheckedAsync(["--empty", "--pages"], inputPaths, [.. inputPaths, EndOfPages, outputPath], outputPath, ct)
            .ConfigureAwait(false);
        RequireOutput(run, outputPath);
    }

    /// <summary>The number of pages qpdf reads in the PDF (<c>qpdf --show-npages</c>).</summary>
    /// <exception cref="PdfException">As for <see cref="TransformAsync"/>.</exception>
    public async Task<int> CountPagesAsync(string inputPath, CancellationToken ct)
    {
        var run = await RunCheckedAsync(["--show-npages"], [inputPath], [inputPath], null, ct).ConfigureAwait(false);
        return int.TryParse(run.StandardOutput.AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pages)
            ? pages
            : throw new QpdfException(run.ExitCode, $"it printed no page count. {Detail(run)}");
    }

    /// <summary>
    /// Runs <c>qpdf --split-pages</c>: each page of the input becomes <c>&lt;n&gt;.pdf</c> in
    /// <paramref name="outputFolder"/>, n counting from 1, zero-padded by qpdf to the digits of the page count
    /// (<c>01.pdf</c> … <c>12.pdf</c>). qpdf creates the files by name and writes through whatever is there by that name,
    /// so the folder must be one nobody else can add to.
    /// </summary>
    /// <exception cref="PdfException">
    /// As for <see cref="TransformAsync"/>, and <see cref="PdfError.UnsupportedFileName"/> when the folder's path holds
    /// <c>%d</c>: qpdf puts the page number in place of the first <c>%d</c> of the output path, wherever it is, and
    /// would write the pages into another folder.
    /// </exception>
    /// <exception cref="IOException">qpdf could not write a page.</exception>
    public async Task SplitPagesAsync(string inputPath, string outputFolder, CancellationToken ct)
    {
        RequireArgumentLine(outputFolder, nameof(outputFolder));
        if (ToExtendedPath(outputFolder).Contains(PageNumber, StringComparison.Ordinal))
            throw new PdfException(PdfError.UnsupportedFileName, PageNumberInPathMessage);
        var pattern = Path.Join(outputFolder, PageNumber + ".pdf");
        await RunCheckedAsync(["--split-pages"], [inputPath], [inputPath, pattern], outputFolder, ct).ConfigureAwait(false);
    }

    // qpdf's placeholder for the page number in the output name of --split-pages.
    private const string PageNumber = "%d";

    internal const string PageNumberInPathMessage =
        "The path of this PDF has \"%d\" in a folder name, which Flevert's PDF tool reads as a page number: rename that folder or move the PDF, and try again.";

    // Ends the list of files after --pages. Not a path: it goes through @- as it is.
    private const string EndOfPages = "--";

    private static void RequireOutput(ExternalToolResult run, string outputPath)
    {
        if (!File.Exists(outputPath))
            throw new QpdfException(run.ExitCode, $"it reported success but wrote no output. {Detail(run)}");
    }

    /// <summary>
    /// Runs qpdf with <paramref name="options"/> on its command line and <paramref name="lines"/> (paths, or
    /// <see cref="EndOfPages"/>) through <c>@-</c>, and returns the run if it succeeded (exit code 0 or 3); otherwise
    /// throws the error the user can act on. <paramref name="inputs"/> are the files qpdf reads; with several, an error
    /// that comes from one of them is a <see cref="BatchInputException"/> naming it. <paramref name="output"/>, when
    /// qpdf's error names it (or a file in it), makes the failure an <see cref="IOException"/>.
    /// </summary>
    private async Task<ExternalToolResult> RunCheckedAsync(
        string[] options, IReadOnlyList<string> inputs, string[] lines, string? output, CancellationToken ct)
    {
        // The options stay on the command line, where qpdf's C runtime would expand them as wildcards.
        if (options.FirstOrDefault(option => option.AsSpan().ContainsAny('*', '?')) is { } wild)
            throw new ArgumentException($"qpdf would expand the wildcard in the option {wild}.", nameof(options));
        // qpdf reads "@file" as more arguments from that file, which nothing here would have checked.
        if (options.FirstOrDefault(option => option.StartsWith('@')) is { } file)
            throw new ArgumentException($"qpdf would read the option {file} as a file of options.", nameof(options));
        // qpdf has no end-of-options marker ("--" is not one): a relative path such as "-x.pdf" would be an option.
        foreach (var line in lines.Where(line => line != EndOfPages))
            RequireArgumentLine(line, nameof(lines));
        RequireExe();

        long total = 0;
        foreach (var input in inputs)
        {
            var length = new FileInfo(input).Length;
            if (length > MaxInputBytes)
                throw ForInput(inputs, input, PdfDocument.TooLargeError(MaxInputBytes));
            total += length;
        }

        if (total > MaxInputBytes)
        {
            throw new PdfException(PdfError.TooLarge,
                $"Together these PDFs are larger than {PdfDocument.DescribeBytes(MaxInputBytes)}, the most Flevert works with at once.");
        }

        var maxMemory = MaxMemoryBytes ?? MaxMemoryFor(total);
        var run = await RunWithLinesAsync(options, lines, TimeoutFor(total), maxMemory, ct).ConfigureAwait(false);
        if (run.ExitCode is 0 or ExitWarnings)
            return run;

        var detail = Detail(run);
        if (run.ExitCode != ExitError)
            throw new QpdfException(run.ExitCode, detail);
        // qpdf names the file it failed on, as it was given. With one input it can only be that one; with several, the
        // input named in its error (the last line), and if it names none, each of them is asked about a password.
        var named = inputs.Count == 1 ? inputs[0] : NamedInput(run, inputs);
        foreach (var input in named is null ? inputs : [named])
        {
            if (await RequiresPasswordAsync(input, maxMemory, ct).ConfigureAwait(false))
                throw ForInput(inputs, input, new PdfException(PdfError.PasswordRequired, PdfException.NeedsPasswordMessage));
        }

        // The output's name is a fresh temporary one no input contains.
        if (output is not null && run.StandardError.Contains(ToExtendedPath(output), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"qpdf could not write the output: {detail}", new QpdfException(run.ExitCode, detail));
        throw ForInput(inputs, named, new PdfException(PdfError.NotPdf, PdfDocument.NotPdfMessage, new QpdfException(run.ExitCode, detail)));
    }

    // The input whose path, as qpdf was given it, starts a message in qpdf's error ("qpdf: <path>: …", "qpdf: open <path>:
    // …"). Every path given is in the \\?\ form, so one cannot be found inside another.
    private static string? NamedInput(ExternalToolResult run, IReadOnlyList<string> inputs)
    {
        var error = run.StandardError.TrimEnd().Split('\n')[^1];
        return inputs.FirstOrDefault(input => error.Contains(ToExtendedPath(input) + ":", StringComparison.Ordinal));
    }

    // Among several inputs, the error of one names it, so the notification says which file stopped the batch.
    private static Exception ForInput(IReadOnlyList<string> inputs, string? input, PdfException error) =>
        inputs.Count > 1 && input is not null ? BatchInputException.ForInput(input, error.Message, error) : error;

    // Asked only after a failure, so a PDF that opens costs no second process. qpdf documents these exit codes, while
    // its error text ("invalid password") may change between versions.
    private async Task<bool> RequiresPasswordAsync(string inputPath, ulong maxMemory, CancellationToken ct)
    {
        var probe = await RunWithLinesAsync(["--requires-password"], [inputPath], ProbeTimeout, maxMemory, ct).ConfigureAwait(false);
        return probe.ExitCode == ExitPasswordRequired;
    }

    private async Task<ExternalToolResult> RunWithLinesAsync(
        string[] options, string[] paths, TimeSpan timeout, ulong maxMemory, CancellationToken ct)
    {
        byte[] lines;
        try
        {
            lines = StrictUtf8.GetBytes(string.Concat(paths.Select(path => (path == EndOfPages ? path : ToExtendedPath(path)) + "\n")));
        }
        catch (EncoderFallbackException e)
        {
            throw new PdfException(PdfError.UnsupportedFileName, UnsupportedFileNameMessage, e);
        }

        // qpdf starts no process of its own, so it may not start one.
        var limits = new ExternalToolLimits { MaxMemoryBytes = maxMemory, SingleProcess = true };
        var run = await ExternalTool.RunAsync(ExePath, [.. options, "@-"], timeout, lines, limits: limits,
            environment: ToolEnvironment(), cancellationToken: ct).ConfigureAwait(false);
        if (Untrustworthy(run, maxMemory) is { } error)
            throw error;
        return run;
    }

    /// <summary>
    /// The error for a run whose result cannot be trusted, whatever its exit code, or <c>null</c>. Refused memory, qpdf
    /// may crash, fail, or treat the object or stream it was reading as null and still report success with warnings
    /// (exit code 3; all three seen with 12.4.2).
    /// </summary>
    /// <remarks>
    /// The job's report of a refused allocation is best effort, and a machine out of memory or an outer job refuses
    /// without one, so qpdf's own words count too: it appends the exception's text after ": " (MSVC: "bad allocation";
    /// libstdc++: "std::bad_alloc"), and no Windows file name in its messages can hold a ":". Only the first megabyte of
    /// stderr is kept, so a success with more than that is not trusted either.
    /// </remarks>
    internal static PdfException? Untrustworthy(ExternalToolResult run, ulong maxMemoryBytes)
    {
        var refused = run.MemoryLimitExceeded
            || run.StandardError.Contains(": bad allocation", StringComparison.OrdinalIgnoreCase)
            || run.StandardError.Contains(": std::bad_alloc", StringComparison.OrdinalIgnoreCase);
        if (refused)
            return new PdfException(PdfError.NeedsTooMuchMemory, NeedsTooMuchMemoryMessage(maxMemoryBytes), new QpdfException(run.ExitCode, Detail(run)));
        if (run.StandardErrorTruncated && run.ExitCode is 0 or ExitWarnings)
        {
            return new PdfException(PdfError.NotPdf, PdfDocument.NotPdfMessage,
                new QpdfException(run.ExitCode, "more than a megabyte of warnings; the rest was not read. " + Detail(run)));
        }

        return null;
    }

    // The limit grows with the file, so the message names the one this file had: "1.6 GB", or "64 MB" under 1 GB.
    internal static string NeedsTooMuchMemoryMessage(ulong maxMemoryBytes)
    {
        var limit = maxMemoryBytes >= 1UL << 30
            ? string.Create(CultureInfo.InvariantCulture, $"{maxMemoryBytes / (double)(1UL << 30):0.#} GB")
            : string.Create(CultureInfo.InvariantCulture, $"{maxMemoryBytes >> 20} MB");
        return $"This PDF needs more memory than Flevert allows for it ({limit}): it is too large or too complex to process.";
    }

    /// <summary>
    /// The <c>\\?\</c> form of a fully qualified path, which qpdf opens at any length; without it qpdf cannot open a path
    /// past 260 characters (nor in the <c>\\.\</c> form; checked with 12.4.2), and the runner shortens an input under 260
    /// characters while the temporary output beside it is 51 characters longer. The prefix turns off Windows'
    /// normalisation, so the path is normalised first, as .NET does when it opens the same path.
    /// </summary>
    /// <remarks>
    /// .NET opens <c>\\?\</c> and <c>\??\</c> (its NT form) paths as they are, so they stay unnormalised. Every other form,
    /// <c>//?/</c> and <c>\\.\</c> included, goes through <see cref="Path.GetFullPath(string)"/> first, which also drops
    /// the trailing dots and spaces Windows ignores in a name.
    /// </remarks>
    internal static string ToExtendedPath(string path)
    {
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            return ExtendedPrefix + path[4..];
        var full = Path.GetFullPath(path);
        // GetFullPath turns "//?/" into "\\?\" and normalises what follows, as Windows does when it opens that form.
        if (full.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            return full;
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal))
            return ExtendedPrefix + full[4..];
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? ExtendedUncPrefix + full[2..] : ExtendedPrefix + full;
    }

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

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
    // the error itself, or, after a megabyte of warnings, the last warnings kept. qpdf names files as it was given them,
    // always with the extended prefix (ToExtendedPath); the message shows them as the user knows them.
    internal static string Detail(ExternalToolResult run)
    {
        var text = run.StandardError.Trim()
            .Replace(ExtendedUncPrefix, @"\\", StringComparison.OrdinalIgnoreCase)
            .Replace(ExtendedPrefix, "", StringComparison.Ordinal);
        if (text.Length == 0)
            text = string.Create(CultureInfo.InvariantCulture, $"no message, exit code {run.ExitCode}");
        if (text.Length <= MaxDetailChars)
            return text;
        var start = text.Length - MaxDetailChars;
        // Never the second half of a character outside the BMP (an emoji in a file name, say) without its first.
        if (char.IsLowSurrogate(text[start]))
            start++;
        return "…" + text[start..];
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
