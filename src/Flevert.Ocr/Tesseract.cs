using System.Globalization;
using Flevert.Core;

namespace Flevert.Ocr;

/// <summary>Runs one tool the way <see cref="ExternalTool.RunAsync"/> does.</summary>
internal delegate Task<ExternalToolResult> RunTool(
    string toolPath,
    IReadOnlyList<string> arguments,
    TimeSpan timeout,
    Action<string>? standardOutputLine,
    CancellationToken ct);

/// <summary>
/// Safe wrapper around <c>tesseract.exe</c> (v5.5.3, Apache-2.0, ADR-0011/docs/specs/ocr.md) run as an isolated process
/// in a Windows job object with memory caps, single process restriction, and BelowNormal priority.
/// Callers must guarantee that only paths Flevert created in its own temporary folders are passed.
/// </summary>
internal sealed class Tesseract
{
    public const string FolderName = "tesseract";

    /// <summary>
    /// The most memory one tesseract process may commit: 1 GB.
    /// Tesseract processes single-page images (typically 300 DPI A4, ~8.7 MP, ~9 MB grayscale).
    /// Even with internal morphology filters, LSTM tensors, and scale spaces, normal usage is under
    /// 150 MB. 1 GB provides a >6x safety margin while capping memory exhaustion bombs.
    /// </summary>
    internal const ulong MaxMemoryBytes = 1UL << 30;

    /// <summary>
    /// How many tesseract processes may run at once in this process: one per core (OMP_THREAD_LIMIT=1), but never more
    /// caps of <see cref="MaxMemoryBytes"/> than a quarter of this PC's memory, as for pictures (ADR-0006).
    /// </summary>
    internal static int MaxParallel { get; } = (int)Math.Clamp(
        (ulong)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4 / MaxMemoryBytes, 1, (ulong)Environment.ProcessorCount);

    /// <summary>
    /// Every tesseract run in this process waits here, whichever engine starts it: a batch of pictures (up to 8 at once,
    /// whose memory budget counts only ImageMagick's pixels) and the pages of a PDF share <see cref="MaxParallel"/>, so
    /// at most that many caps of <see cref="MaxMemoryBytes"/> are ever committed together.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(MaxParallel, MaxParallel);

    /// <summary>
    /// Maximum time allowed for one OCR page operation: 150 seconds (120 s base per page plus 30 s margin).
    /// Typical 300 DPI pages finish in 1-10 seconds on modern CPUs. 150 seconds provides ample margin
    /// for low-power or heavily loaded machines while ensuring hung processes or catastrophic backtracking
    /// in layout analysis are terminated. Time spent waiting for <see cref="Gate"/> does not count.
    /// </summary>
    public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(150);

    private const int MaxDetailChars = 2_000;

    // SingleProcess: Tesseract does not spawn child processes.
    // BelowNormalPriority: Keeps machine responsive during multi-core batch OCR.
    // MaxMemoryBytes: Caps allocations to prevent memory exhaustion attacks.
    private static readonly ExternalToolLimits Limits = new()
    {
        BelowNormalPriority = true,
        SingleProcess = true,
        MaxMemoryBytes = MaxMemoryBytes,
    };

    /// <param name="folder">Folder holding tesseract.exe and tessdata/.</param>
    public Tesseract(string folder)
        : this(Path.Combine(folder, "tesseract.exe"), Path.Combine(folder, "tessdata"), null)
    {
    }

    internal Tesseract(string executablePath, string tessDataPath, RunTool? run = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tessDataPath);
        ExecutablePath = executablePath;
        TessDataPath = tessDataPath;
        Run = run ?? RunDefault;
    }

    /// <summary>
    /// The <c>tesseract</c> folder beside the running program: in the install folder for flevert.exe
    /// or in the build output, where Flevert.Ocr copies it.
    /// </summary>
    public static Tesseract NextToApp { get; } = new(Path.Combine(AppContext.BaseDirectory, FolderName));

    public string ExecutablePath { get; }

    public string TessDataPath { get; }

    internal RunTool Run { get; }

    private Task<ExternalToolResult> RunDefault(
        string toolPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        Action<string>? standardOutputLine,
        CancellationToken ct) =>
        ExternalTool.RunAsync(
            toolPath,
            arguments,
            timeout,
            limits: Limits,
            environment: ToolEnvironment(TessDataPath),
            standardOutputLine: standardOutputLine,
            cancellationToken: ct);

    /// <summary>
    /// Runs tesseract once <see cref="Gate"/> lets it; cancelling <paramref name="ct"/> also ends the wait.
    /// </summary>
    private async Task<ExternalToolResult> RunGatedAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Run(ExecutablePath, arguments, PageTimeout, null, ct).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            throw new OcrException(OcrException.TooSlowMessage, e);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Sets TESSDATA_PREFIX to Flevert's own tessdata folder (so user environment variable never leaks in)
    /// and OMP_THREAD_LIMIT=1 so page-level parallel processing does not cause thread contention.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> ToolEnvironment(string tessdataPath)
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["TESSDATA_PREFIX"] = tessdataPath,
            ["OMP_THREAD_LIMIT"] = "1",
        };
    }

    /// <summary>
    /// Runs Tesseract on <paramref name="imagePath"/> and returns the recognised UTF-8 text.
    /// Default languages are Vietnamese and English (<c>-l vie+eng</c>), LSTM engine (<c>--oem 1</c>),
    /// fully automatic page segmentation (<c>--psm 3</c>).
    /// </summary>
    /// <param name="imagePath">Absolute path of a trusted image file prepared by Flevert in a temporary directory.</param>
    /// <param name="ct">Token to cancel the OCR operation; kills the running process immediately.</param>
    /// <returns>The recognised text in UTF-8.</returns>
    /// <exception cref="OcrException">Tesseract failed, ran out of memory, or timed out.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public async Task<string> ReadTextAsync(string imagePath, CancellationToken ct)
    {
        CheckInputPath(imagePath);
        RequireTool();

        string[] arguments = [imagePath, "stdout", "-l", "vie+eng", "--oem", "1", "--psm", "3"];
        var run = await RunGatedAsync(arguments, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        RequireMemory(run);
        CheckRunSuccess(run);
        return run.StandardOutput;
    }

    /// <summary>
    /// Runs Tesseract on <paramref name="imagePath"/> to produce an invisible text-only PDF layer at
    /// <paramref name="outputBase"/><c>.pdf</c> with page dimensions matching the image DPI, and returns the text in that
    /// layer, which the same run writes to <paramref name="outputBase"/><c>.txt</c>: blank when it found none, so that a
    /// caller adds no empty layer.
    /// </summary>
    /// <param name="imagePath">Absolute path of a trusted image file prepared by Flevert in a temporary directory.</param>
    /// <param name="outputBase">Output base path without extension (or with <c>.pdf</c>). Tesseract produces <c>&lt;outputBase&gt;.pdf</c>.</param>
    /// <param name="ct">Token to cancel the OCR operation; kills the running process immediately.</param>
    /// <exception cref="OcrException">Tesseract failed, ran out of memory, timed out, or the output PDF was not produced.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public async Task<string> WriteTextLayerAsync(string imagePath, string outputBase, CancellationToken ct) =>
        (await ReadIntoTextLayerAsync(imagePath, outputBase, ct).ConfigureAwait(false)).Text;

    /// <summary>
    /// As <see cref="WriteTextLayerAsync"/>, with how sure tesseract was of the layer's words, from the same run (which
    /// writes them to <paramref name="outputBase"/><c>.tsv</c>).
    /// </summary>
    internal Task<OcrReading> ReadIntoTextLayerAsync(string imagePath, string outputBase, CancellationToken ct) =>
        ReadToFilesAsync(imagePath, outputBase, textLayer: true, ct);

    /// <summary>
    /// Reads <paramref name="imagePath"/> as <see cref="ReadTextAsync"/> does, and returns the text with how sure
    /// tesseract was of its words, both from the same run, which writes them to <paramref name="outputBase"/><c>.txt</c>
    /// and <c>.tsv</c>.
    /// </summary>
    /// <param name="imagePath">Absolute path of a trusted image file prepared by Flevert in a temporary directory.</param>
    /// <param name="outputBase">Output base path without extension, in a folder of Flevert's own.</param>
    /// <param name="ct">Token to cancel the OCR operation; kills the running process immediately.</param>
    /// <exception cref="OcrException">Tesseract failed, ran out of memory, timed out, or wrote no text.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    internal Task<OcrReading> ReadAsync(string imagePath, string outputBase, CancellationToken ct) =>
        ReadToFilesAsync(imagePath, outputBase, textLayer: false, ct);

    private async Task<OcrReading> ReadToFilesAsync(string imagePath, string outputBase, bool textLayer, CancellationToken ct)
    {
        CheckInputPath(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBase);
        RequireTool();

        var baseName = outputBase.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? outputBase[..^4]
            : outputBase;
        var expectedPdfPath = baseName + ".pdf";
        var textPath = baseName + ".txt";
        var wordsPath = baseName + ".tsv";

        List<string> arguments = [imagePath, baseName, "-l", "vie+eng", "--oem", "1", "--psm", "3"];
        if (textLayer)
            arguments.AddRange(["-c", "textonly_pdf=1"]);
        // The text, and each word with tesseract's confidence in it, from the same reading: tessdata has no "txt" or "tsv"
        // config file, so these are set directly.
        arguments.AddRange(["-c", "tessedit_create_txt=1", "-c", "tessedit_create_tsv=1"]);
        if (textLayer)
            arguments.Add("pdf");

        var run = await RunGatedAsync(arguments, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        RequireMemory(run);
        CheckRunSuccess(run);

        if (textLayer && !File.Exists(expectedPdfPath))
        {
            throw new OcrException(
                OcrException.ToolFailedMessage,
                new TesseractException("tesseract", run.ExitCode, $"Expected output PDF not found: {expectedPdfPath}"));
        }

        foreach (var output in (string[])[textPath, wordsPath])
        {
            if (!File.Exists(output))
            {
                throw new OcrException(
                    OcrException.ToolFailedMessage,
                    new TesseractException("tesseract", run.ExitCode, $"Expected output text not found: {output}"));
            }
        }

        var text = await File.ReadAllTextAsync(textPath, ct).ConfigureAwait(false);
        var words = await File.ReadAllTextAsync(wordsPath, ct).ConfigureAwait(false);
        return new OcrReading(text, ReadingQuality.Parse(words));
    }

    /// <summary>
    /// The OSD confidence from which its turn is taken on trust: 14, OCRmyPDF's default <c>rotate_pages_threshold</c>.
    /// Under it, a caller checks the turn by reading the page both ways (docs/specs/ocr.md).
    /// </summary>
    public const double ConfidentOsd = 14;

    /// <summary>
    /// Detects orientation and script of <paramref name="imagePath"/> using OSD (<c>--psm 0 -l osd</c>).
    /// Parses "Rotate: N" and "Orientation confidence: C". Returns N when confidence C >= <see cref="ConfidentOsd"/>, else 0.
    /// </summary>
    /// <param name="imagePath">Absolute path of a trusted image file prepared by Flevert in a temporary directory.</param>
    /// <param name="ct">Token to cancel the operation; kills the running process immediately.</param>
    /// <returns>Degrees to rotate clockwise (0, 90, 180, 270) to make the text upright; 0 if confidence &lt; 14 or undetected.</returns>
    /// <exception cref="OcrException">Tesseract crashed, ran out of memory, or timed out.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    public async Task<int> DetectRotationAsync(string imagePath, CancellationToken ct) =>
        Confident(await DetectOrientationAsync(imagePath, ct).ConfigureAwait(false));

    /// <summary>
    /// OSD's answer for <paramref name="imagePath"/> however unsure it is: no turn and no confidence when the page has too
    /// few characters for OSD.
    /// </summary>
    /// <exception cref="OcrException">Tesseract crashed, ran out of memory, or timed out.</exception>
    /// <exception cref="OperationCanceledException">The operation was cancelled.</exception>
    internal async Task<OsdAnswer> DetectOrientationAsync(string imagePath, CancellationToken ct)
    {
        CheckInputPath(imagePath);
        RequireTool();

        string[] arguments = [imagePath, "stdout", "--psm", "0", "-l", "osd"];
        var run = await RunGatedAsync(arguments, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        RequireMemory(run);

        // When page has too few characters or layout is ambiguous, OSD exits with code 1 or warning; return 0.
        if (run.ExitCode != 0)
        {
            if (run.StandardError.Contains("Too few characters", StringComparison.OrdinalIgnoreCase) ||
                run.StandardOutput.Contains("Too few characters", StringComparison.OrdinalIgnoreCase))
            {
                return default;
            }

            CheckRunSuccess(run);
        }

        return ParseOsd(run.StandardOutput);
    }

    internal static int ParseOsdRotation(string output) => Confident(ParseOsd(output));

    private static int Confident(OsdAnswer orientation) => orientation.Confidence >= ConfidentOsd ? orientation.Rotate : 0;

    internal static OsdAnswer ParseOsd(string output)
    {
        int? rotate = null;
        double? confidence = null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Rotate:", StringComparison.OrdinalIgnoreCase))
            {
                var val = line.AsSpan("Rotate:".Length).Trim();
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                    rotate = r;
            }
            else if (line.StartsWith("Orientation confidence:", StringComparison.OrdinalIgnoreCase))
            {
                var val = line.AsSpan("Orientation confidence:".Length).Trim();
                if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && double.IsFinite(c))
                    confidence = c;
            }
        }

        // Tesseract only ever answers a quarter turn; anything else is not an answer to act on.
        var degrees = rotate is 0 or 90 or 180 or 270 ? rotate.Value : 0;
        return new OsdAnswer(degrees, Math.Max(confidence ?? 0, 0));
    }

    private void RequireTool()
    {
        if (!File.Exists(ExecutablePath))
        {
            throw new FileNotFoundException(
                $"{Path.GetFileName(ExecutablePath)} is missing from {Path.GetDirectoryName(ExecutablePath)}. Reinstall Flevert (in a source build, run scripts\\fetch-deps.ps1).",
                ExecutablePath);
        }

        if (!Directory.Exists(TessDataPath))
        {
            throw new DirectoryNotFoundException(
                $"tessdata folder is missing from {TessDataPath}. Reinstall Flevert (in a source build, run scripts\\fetch-deps.ps1).");
        }
    }

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".tif",
        ".tiff",
        ".pbm",
        ".pgm",
        ".ppm",
    };

    private static void CheckInputPath(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!Path.IsPathFullyQualified(imagePath))
            throw new ArgumentException($"The image path must be fully qualified: {imagePath}", nameof(imagePath));

        var extension = Path.GetExtension(imagePath);
        if (!AllowedExtensions.Contains(extension))
        {
            throw new ArgumentException(
                $"Unsupported image format for OCR: \"{extension}\". Only .png, .tif, .tiff, .pbm, .pgm, and .ppm are accepted.",
                nameof(imagePath));
        }

        if (!File.Exists(imagePath))
            throw new ArgumentException($"Image file not found: {imagePath}", nameof(imagePath));
    }

    private static void RequireMemory(ExternalToolResult run)
    {
        if (run.MemoryLimitExceeded)
        {
            throw new OcrException(
                OcrException.NeedsTooMuchMemoryMessage,
                new TesseractException("tesseract", run.ExitCode, Detail(run)));
        }
    }

    private static void CheckRunSuccess(ExternalToolResult run)
    {
        if (run.ExitCode == 0 && !run.StandardOutputTruncated)
            return;

        if (run.ExitCode != 0 && run.StandardError.Contains("No space left on device", StringComparison.Ordinal))
            throw new OcrException(OcrException.DiskFullMessage, new TesseractException("tesseract", run.ExitCode, Detail(run)));

        if (run.ExitCode != 0 && (run.StandardError.Contains("Failed loading language", StringComparison.OrdinalIgnoreCase) ||
                                  run.StandardError.Contains("couldn't load any languages", StringComparison.OrdinalIgnoreCase)))
            throw new OcrException(OcrException.MissingDataMessage, new TesseractException("tesseract", run.ExitCode, Detail(run)));

        var detail = run.StandardOutputTruncated ? "standard output truncated" : Detail(run);
        throw new OcrException(OcrException.ToolFailedMessage, new TesseractException("tesseract", run.ExitCode, detail));
    }

    internal static string Detail(ExternalToolResult run)
    {
        var text = run.StandardError.Trim();
        if (text.Length == 0)
            return string.Create(CultureInfo.InvariantCulture, $"no message, exit code {run.ExitCode}");
        if (text.Length <= MaxDetailChars)
            return text;
        var start = text.Length - MaxDetailChars;
        if (char.IsLowSurrogate(text[start]))
            start++;
        return "…" + text[start..];
    }
}
