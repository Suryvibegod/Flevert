using System.Globalization;
using System.Security.Cryptography;
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
        var run = await RunCheckedAsync([KeepStreamsAsTheyAre, "--empty", "--pages"], inputPaths, [.. inputPaths, EndOfPages, outputPath], outputPath, ct)
            .ConfigureAwait(false);
        RequireOutput(run, outputPath);
    }

    /// <summary>
    /// The number of pages qpdf finds in the PDF's page tree, refusing a PDF with more than
    /// <see cref="PdfDocument.MaxPages"/> (docs/specs/pdf.md: every operation works on at most 2 000 pages).
    /// </summary>
    /// <remarks>
    /// <c>--show-npages</c> only prints the root's <c>/Count</c>, which a PDF may set to anything (T18: 100 000 pages under
    /// a /Count of 1 went through, and split then wrote 100 000 files before failing). <c>--show-pages</c> walks the
    /// tree as every other qpdf operation does, and prints a <c>page n: …</c> line for each page; qpdf is stopped at the
    /// first page past the limit, and a tree with a loop, or nested too deep, fails as damaged.
    /// </remarks>
    /// <exception cref="PdfException">
    /// As for <see cref="TransformAsync"/>, and <see cref="PdfError.TooManyPages"/> past the limit.
    /// </exception>
    public async Task<int> CountPagesAsync(string inputPath, CancellationToken ct)
    {
        var pages = 0;
        void OnLine(string line)
        {
            if (line.StartsWith("page ", StringComparison.Ordinal) && ++pages > PdfDocument.MaxPages)
                throw TooManyPagesError();
        }

        await RunCheckedAsync(["--show-pages"], [inputPath], [inputPath], null, ct, outputLine: OnLine).ConfigureAwait(false);
        return pages;
    }

    /// <summary>The error for a PDF past <see cref="PdfDocument.MaxPages"/>, whose exact count was not read.</summary>
    internal static PdfException TooManyPagesError() => new(PdfError.TooManyPages, string.Create(CultureInfo.InvariantCulture,
        $"This PDF has more than {PdfDocument.MaxPages} pages; Flevert works with at most {PdfDocument.MaxPages}."));

    /// <summary>
    /// The option every qpdf operation that only moves pages around passes (rotate, merge, split): streams are copied as
    /// they are, never decoded nor compressed. By default qpdf decodes a stream whose filters it would not keep as they are
    /// (two Flate filters, say) and compresses it again, so a 10 KB stream that inflates to 4 GB took 15 to 20 s to rotate
    /// (T18); <c>--decode-level=none</c> alone still took 16 s, since compressing streams (<c>--compress-streams=y</c>,
    /// the default) decodes such a stream to compress it once. <c>preserve</c> turns off both, and the bomb rotates in
    /// 0.04 s. Nothing is lost, and nothing is compressed that was not: the pages are the same bytes.
    /// </summary>
    public const string KeepStreamsAsTheyAre = "--stream-data=preserve";

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
        await RunCheckedAsync([KeepStreamsAsTheyAre, "--split-pages"], [inputPath], [inputPath, pattern], outputFolder, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <c>qpdf --update-from-json=&lt;json&gt; &lt;input&gt; &lt;output&gt;</c>: the input with the objects that
    /// <paramref name="jsonPath"/> (qpdf JSON version 2) gives replacing its own, written to the output. The option goes
    /// through <c>@-</c> with the two paths, its path in the <c>\\?\</c> form like theirs; on the command line qpdf would
    /// expand that path's <c>?</c> as a wildcard.
    /// </summary>
    /// <param name="jsonPath">Fully qualified; in a folder nobody else can change, since qpdf reads it as it is.</param>
    /// <exception cref="PdfException">As for <see cref="TransformAsync"/>.</exception>
    /// <exception cref="IOException">qpdf could not write <paramref name="outputPath"/>.</exception>
    public async Task UpdateFromJsonAsync(string inputPath, string jsonPath, string outputPath, CancellationToken ct)
    {
        RequireArgumentLine(jsonPath, nameof(jsonPath));
        var run = await RunCheckedAsync([], [inputPath], [UpdateFromJson + jsonPath, inputPath, outputPath], outputPath, ct)
            .ConfigureAwait(false);
        RequireOutput(run, outputPath);
    }

    /// <summary>Whether the PDF is encrypted, and whether it opens without a password (<c>qpdf --requires-password</c>).</summary>
    /// <exception cref="PdfException">The file is not a PDF, or is damaged beyond repair, or too large.</exception>
    public async Task<PdfEncryption> GetEncryptionAsync(string inputPath, CancellationToken ct)
    {
        RequireArgumentLine(inputPath, nameof(inputPath));
        RequireExe();
        var length = new FileInfo(inputPath).Length;
        if (length > MaxInputBytes)
            throw PdfDocument.TooLargeError(MaxInputBytes);

        var run = await RunWithLinesAsync(["--requires-password"], [inputPath], TimeoutFor(length), MaxMemoryBytes ?? MaxMemoryFor(length), ct)
            .ConfigureAwait(false);
        // Documented exit codes: 0 a password is needed, 2 not encrypted, 3 encrypted but it opens. 2 is also qpdf's
        // error code, and then stderr ends with qpdf's error ("qpdf: <path>: …") rather than warnings ("WARNING: …").
        return run.ExitCode switch
        {
            ExitPasswordRequired => PdfEncryption.NeedsPassword,
            ExitWarnings => PdfEncryption.OpensWithoutPassword,
            ExitError when !run.StandardError.TrimEnd().Split('\n')[^1].StartsWith("qpdf: ", StringComparison.Ordinal) => PdfEncryption.None,
            _ => throw new PdfException(PdfError.NotPdf, PdfDocument.NotPdfMessage, new QpdfException(run.ExitCode, Detail(run))),
        };
    }

    /// <summary>
    /// Writes <paramref name="outputPath"/>: the input encrypted with AES-256, user and owner password both
    /// <paramref name="password"/>, nothing restricted. The password reaches qpdf only on its standard input.
    /// </summary>
    /// <exception cref="PdfException">As for <see cref="TransformAsync"/>.</exception>
    /// <exception cref="ArgumentException">The password holds a line break, a null character or a lone surrogate
    /// (<see cref="PasswordRules"/> keeps them out).</exception>
    public async Task EncryptAsync(string inputPath, string outputPath, PdfPassword password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);
        // The passwords come before --bits: qpdf 12.4.2 reads --user-password after it as an unrecognized argument, and
        // repeats it in its error. "--" ends the encryption options.
        var secret = new Secret(password, EncryptOptionsBeforePasswords, ["--user-password=", "--owner-password="], ["--bits=256", EndOfPages]);
        var run = await RunCheckedAsync(["--encrypt"], [inputPath], [inputPath, outputPath], outputPath, ct, secret).ConfigureAwait(false);
        RequireOutput(run, outputPath);
    }

    /// <summary>
    /// Writes <paramref name="outputPath"/>: the input opened with <paramref name="password"/>, without encryption. The
    /// password reaches qpdf only on its standard input.
    /// </summary>
    /// <exception cref="PdfException"><see cref="PdfError.WrongPassword"/> when the password does not open it; otherwise as
    /// for <see cref="TransformAsync"/>.</exception>
    /// <exception cref="ArgumentException">As for <see cref="EncryptAsync"/>.</exception>
    public async Task DecryptAsync(string inputPath, string outputPath, PdfPassword password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(password);
        var secret = new Secret(password, [], ["--password="], []);
        var run = await RunCheckedAsync(["--decrypt"], [inputPath], [inputPath, outputPath], outputPath, ct, secret, opening: password)
            .ConfigureAwait(false);
        RequireOutput(run, outputPath);
    }

    // An option whose value is a path: given through @- like the paths, as "<option><\\?\ path>".
    private const string UpdateFromJson = "--update-from-json=";

    // The path in a line of @-: the line itself, or the value of a path option. A path is fully qualified, so it never
    // starts like the option.
    private static string PathIn(string line) =>
        line.StartsWith(UpdateFromJson, StringComparison.Ordinal) ? line[UpdateFromJson.Length..] : line;

    /// <summary>
    /// Lines of <c>@-</c> that carry the password, before the paths: <see cref="Before"/>, then each of
    /// <see cref="Prefixes"/> followed by the password (<c>--password=…</c>), then <see cref="After"/>. The password goes
    /// from its characters straight into the bytes qpdf reads, and those bytes are zeroed once qpdf has exited.
    /// </summary>
    internal sealed record Secret(PdfPassword Password, string[] Before, string[] Prefixes, string[] After);

    /// <summary>What stands in for qpdf's message when it holds the password (qpdf repeats an argument it does not
    /// recognize), so the message never reaches an exception, the log or a notification.</summary>
    internal const string PasswordInMessage = "(qpdf's message was not kept: it could hold the password)";

    /// <summary>
    /// <paramref name="run"/> with any output that holds the password replaced as a whole: replacing only the password
    /// would show where it was, and with it the password.
    /// </summary>
    internal static ExternalToolResult WithoutPassword(ExternalToolResult run, PdfPassword password)
    {
        var chars = password.Chars;
        var inError = run.StandardError.AsSpan().IndexOf(chars) >= 0;
        var inOutput = run.StandardOutput.AsSpan().IndexOf(chars) >= 0;
        return inError || inOutput
            ? run with
            {
                StandardError = inError ? PasswordInMessage : run.StandardError,
                StandardOutput = inOutput ? PasswordInMessage : run.StandardOutput,
            }
            : run;
    }

    /// <summary>For tests: encryption options put on <c>@-</c> before the passwords instead of after them, where qpdf
    /// refuses the passwords as unrecognized arguments and repeats them in its error.</summary>
    internal string[] EncryptOptionsBeforePasswords { get; init; } = [];

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
    /// <param name="secret">Password lines put on <c>@-</c> before <paramref name="lines"/>.</param>
    /// <param name="opening">The password that opens the input, if one was given: the check after a failure uses it, and
    /// a password that does not open the input is <see cref="PdfError.WrongPassword"/>.</param>
    private async Task<ExternalToolResult> RunCheckedAsync(
        string[] options, IReadOnlyList<string> inputs, string[] lines, string? output, CancellationToken ct,
        Secret? secret = null, PdfPassword? opening = null, Action<string>? outputLine = null)
    {
        // The options stay on the command line, where qpdf's C runtime would expand them as wildcards.
        if (options.FirstOrDefault(option => option.AsSpan().ContainsAny('*', '?')) is { } wild)
            throw new ArgumentException($"qpdf would expand the wildcard in the option {wild}.", nameof(options));
        // qpdf reads "@file" as more arguments from that file, which nothing here would have checked.
        if (options.FirstOrDefault(option => option.StartsWith('@')) is { } file)
            throw new ArgumentException($"qpdf would read the option {file} as a file of options.", nameof(options));
        // qpdf has no end-of-options marker ("--" is not one): a relative path such as "-x.pdf" would be an option.
        foreach (var line in lines.Where(line => line != EndOfPages))
            RequireArgumentLine(PathIn(line), nameof(lines));
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
        var run = await RunWithLinesAsync(options, lines, Timeout ?? TimeoutFor(total), maxMemory, ct, secret, outputLine).ConfigureAwait(false);
        if (run.ExitCode is 0 or ExitWarnings)
            return run;

        var detail = Detail(run);
        // qpdf names the file it failed on, as it was given. With one input it can only be that one; with several, the
        // input named in its error (the last line).
        var named = inputs.Count == 1 ? inputs[0] : NamedInput(run, inputs);
        // Any other exit code is not qpdf's: it crashed (a stack overflow is 0xC00000FD) or was ended from outside.
        if (run.ExitCode != ExitError)
            throw ForInput(inputs, named, new PdfException(PdfError.ReaderFailed, ToolFailedMessage, new QpdfException(run.ExitCode, detail)));
        // If it names none, each input is asked about a password.
        foreach (var input in named is null ? inputs : [named])
        {
            bool needsPassword;
            try
            {
                needsPassword = await RequiresPasswordAsync(input, maxMemory, ct, opening).ConfigureAwait(false);
            }
            catch (PdfException e)
            {
                // Too slow, or too much memory, for that input alone: among several, it is the one to name.
                throw ForInput(inputs, input, e);
            }

            if (needsPassword)
            {
                throw ForInput(inputs, input, opening is null
                    ? new PdfException(PdfError.PasswordRequired, PdfException.NeedsPasswordMessage)
                    : new PdfException(PdfError.WrongPassword, PdfException.WrongPasswordMessage));
            }
        }

        // The output's name is a fresh temporary one no input contains.
        if (output is not null && run.StandardError.Contains(ToExtendedPath(output), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"qpdf could not write the output: {detail}", new QpdfException(run.ExitCode, detail));
        if (UnsupportedEncryption(run))
        {
            throw ForInput(inputs, named, new PdfException(PdfError.UnsupportedEncryption, PdfDocument.UnsupportedEncryptionMessage,
                new QpdfException(run.ExitCode, detail)));
        }

        throw ForInput(inputs, named, new PdfException(PdfError.NotPdf, PdfDocument.NotPdfMessage, new QpdfException(run.ExitCode, detail)));
    }

    /// <summary>A timeout instead of <see cref="TimeoutFor"/>, for tests.</summary>
    internal TimeSpan? Timeout { get; init; }

    /// <summary>A timeout instead of the minute the password check after a failure gets, for tests.</summary>
    internal TimeSpan? PasswordProbeTimeout { get; init; }

    internal const string ToolFailedMessage =
        "Flevert's PDF tool stopped on this PDF without finishing: the PDF may be damaged, or made to crash PDF tools.";

    internal const string TooSlowMessage =
        "This PDF took too long to process: it is too large or too complex, or made to stall PDF tools.";

    // qpdf describes the object it failed on as "(encryption dictionary…)" when the encryption is one it cannot read, such
    // as a security handler nobody implements ("unsupported encryption filter"); the same words as pdfium give then, rather
    // than "not a PDF". The text may change between qpdf versions: then the file is "not a PDF, or damaged", still true.
    private static bool UnsupportedEncryption(ExternalToolResult run)
    {
        var error = run.StandardError.TrimEnd().Split('\n')[^1];
        return error.Contains("encryption dictionary", StringComparison.OrdinalIgnoreCase)
            || error.Contains("unsupported encryption", StringComparison.OrdinalIgnoreCase);
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
    // its error text ("invalid password") may change between versions. With a password, 0 means it does not open the
    // file either.
    private async Task<bool> RequiresPasswordAsync(string inputPath, ulong maxMemory, CancellationToken ct, PdfPassword? password = null)
    {
        var secret = password is null ? null : new Secret(password, [], ["--password="], []);
        var probe = await RunWithLinesAsync(["--requires-password"], [inputPath], PasswordProbeTimeout ?? ProbeTimeout, maxMemory, ct, secret)
            .ConfigureAwait(false);
        return probe.ExitCode == ExitPasswordRequired;
    }

    internal async Task<ExternalToolResult> RunWithLinesAsync(
        string[] options, string[] paths, TimeSpan timeout, ulong maxMemory, CancellationToken ct, Secret? secret = null,
        Action<string>? outputLine = null)
    {
        // A line goes to the callback before WithoutPassword can replace it, so a password run never streams qpdf's output.
        if (secret is not null && outputLine is not null)
            throw new InvalidOperationException("A run with a password must not stream qpdf's output.");
        byte[] lines;
        try
        {
            lines = StrictUtf8.GetBytes(string.Concat(paths.Select(path => ToLine(path) + "\n")));
        }
        catch (EncoderFallbackException e)
        {
            throw new PdfException(PdfError.UnsupportedFileName, UnsupportedFileNameMessage, e);
        }

        if (secret is not null)
            lines = WithPasswordLines(secret, lines);
        try
        {
            // qpdf starts no process of its own, so it may not start one.
            var limits = new ExternalToolLimits { MaxMemoryBytes = maxMemory, SingleProcess = true };
            ExternalToolResult run;
            try
            {
                run = await ExternalTool.RunAsync(ExePath, [.. options, "@-"], timeout, lines, limits: limits,
                    environment: ToolEnvironment(), standardOutputLine: outputLine, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (TimeoutException e)
            {
                // qpdf has been killed and has exited.
                throw new PdfException(PdfError.TooSlow, TooSlowMessage, e);
            }

            if (secret is not null)
                run = WithoutPassword(run, secret.Password);
            if (Untrustworthy(run, maxMemory) is { } error)
                throw error;
            return run;
        }
        finally
        {
            // ExternalTool writes these bytes straight to qpdf's pipe and keeps no copy.
            if (secret is not null)
                CryptographicOperations.ZeroMemory(lines);
        }
    }

    // The bytes of @-: the secret's lines, the password encoded from its characters into place, then the paths. Nothing
    // of the password passes through a string.
    private static byte[] WithPasswordLines(Secret secret, byte[] paths)
    {
        var password = secret.Password.Chars;
        if (password.ContainsAny('\r', '\n', '\0'))
            throw new ArgumentException("The password must not contain a line break or a null character.", nameof(secret));
        int passwordBytes;
        try
        {
            passwordBytes = PdfPassword.StrictUtf8.GetByteCount(password);
        }
        catch (EncoderFallbackException)
        {
            // Not the exception: it would carry the character it could not encode.
            throw new ArgumentException("The password has a character UTF-8 cannot hold.", nameof(secret));
        }

        var before = StrictUtf8.GetBytes(string.Concat(secret.Before.Select(line => line + "\n")));
        var after = StrictUtf8.GetBytes(string.Concat(secret.After.Select(line => line + "\n")));
        var prefixes = secret.Prefixes.Select(StrictUtf8.GetBytes).ToArray();
        var bytes = new byte[before.Length + prefixes.Sum(prefix => prefix.Length + passwordBytes + 1) + after.Length + paths.Length];
        var at = bytes.AsSpan();
        before.CopyTo(at);
        at = at[before.Length..];
        foreach (var prefix in prefixes)
        {
            prefix.CopyTo(at);
            at = at[prefix.Length..];
            at = at[PdfPassword.StrictUtf8.GetBytes(password, at)..];
            at[0] = (byte)'\n';
            at = at[1..];
        }

        after.CopyTo(at);
        paths.CopyTo(at[after.Length..]);
        return bytes;
    }

    // A line of @- as qpdf reads it: every path, alone or as an option's value, in the \\?\ form.
    private static string ToLine(string line)
    {
        if (line == EndOfPages)
            return line;
        var path = PathIn(line);
        return line[..^path.Length] + ToExtendedPath(path);
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
