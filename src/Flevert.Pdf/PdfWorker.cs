using System.Diagnostics;
using System.Globalization;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>
/// Runs <c>flevert.exe pdf-worker</c> (ADR-0008, "Reading untrusted PDFs in a worker"): pdfium reads the PDF in a process
/// of its own, so a crash, a page that never finishes or a page that needs too much memory ends that process and fails this
/// file, while the batch leader and the other files of the batch carry on.
/// </summary>
/// <remarks>
/// The worker runs through <see cref="ExternalTool"/>, in a job that caps its memory (<see cref="DefaultMaxMemoryBytes"/>),
/// lets it start no other process, and kills it when flevert.exe ends. A watchdog kills it when it reports nothing new
/// for longer than the step it is on may take (<see cref="StallWindow"/>); cancelling kills it. Its command line is only
/// <c>pdf-worker</c>: the request, paths included, goes through standard input (<see cref="PdfRenderRequest"/>).
/// </remarks>
public sealed class PdfWorker
{
    public static readonly string FileName = OperatingSystem.IsWindows() ? "flevert.exe" : "flevert";

    /// <summary>The worker's command, hidden from the usage text (<see cref="PdfWorkerCommand"/>).</summary>
    internal const string Command = "pdf-worker";

    /// <summary>
    /// The most memory a worker may commit: a page renders into at most 400 MB, ImageMagick copies it once more to encode
    /// it, and pdfium needs room for what it decodes to draw the page (a full-page 100 MP image is another 400 MB), beside
    /// the runtime. A PDF that needs more is refused with "needs more memory" (ADR-0008).
    /// </summary>
    public const ulong DefaultMaxMemoryBytes = 5UL << 29; // 2.5 GB

    /// <summary>What the worker may take for a step without a sign of progress, before the size of the page counts.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Added to the window per megapixel of the page being drawn: 100 MP (10 000 px square) gets 35 s in all.</summary>
    private static readonly TimeSpan PerMegapixel = TimeSpan.FromSeconds(0.25);

    /// <summary>Added to the window for opening the PDF per 100 MB of file: pdfium may scan a damaged file whole.</summary>
    private static readonly TimeSpan PerHundredMegabytes = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A backstop for the whole run, past anything a PDF Flevert accepts can take (2 000 pages at the largest size); the
    /// watchdog is what stops a stalled page.
    /// </summary>
    private static readonly TimeSpan RunTimeout = TimeSpan.FromHours(6);

    /// <param name="exePath">Absolute path of flevert.exe; checked when the worker is run, not here.</param>
    public PdfWorker(string exePath) => ExePath = exePath;

    /// <summary>
    /// flevert.exe in the folder of the running program: the install folder, which setup checks only the user and
    /// administrators can change (the same trust as qpdf.exe beside it).
    /// </summary>
    public static PdfWorker NextToApp { get; } = new(Path.Combine(AppContext.BaseDirectory, FileName));

    public string ExePath { get; }

    /// <summary>A lower limit, for tests.</summary>
    internal ulong MaxMemoryBytes { get; init; } = DefaultMaxMemoryBytes;

    /// <summary>A shorter wait, for tests.</summary>
    internal TimeSpan StallTimeout { get; init; } = DefaultStallTimeout;

    /// <summary>
    /// The whole file's time budget before its pages count (T18, final security audit): the stall window bounds one page,
    /// but 2 000 pages each just inside it would keep a worker busy for hours. Once the worker has said how many pages
    /// the PDF has, the run may take this plus <see cref="FileBudgetPerPage"/> for each, in all; past it the worker is
    /// killed and the file fails with <see cref="OverBudgetMessage"/>. 2 000 pages get 68 minutes; an honest page takes
    /// well under a tenth of a second at 150 DPI on the dev machine, and a 100 MP page 2.5 s.
    /// </summary>
    internal TimeSpan FileBudgetBase { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Added to the whole file's budget per page (<see cref="FileBudgetBase"/>); lower for tests.</summary>
    internal TimeSpan FileBudgetPerPage { get; init; } = TimeSpan.FromSeconds(2);

    internal const string OverBudgetMessage = "This PDF takes too long to convert: its pages are too complex, or made to stall PDF readers.";

    /// <summary>
    /// How long a page of <paramref name="width"/> x <paramref name="height"/> px may go without a sign of progress: the
    /// stall timeout plus a quarter second per megapixel, since honest work grows with the pixels.
    /// </summary>
    internal TimeSpan StallWindow(int width, int height) => StallTimeout + (PerMegapixel * ((double)width * height / 1_000_000));

    /// <summary>
    /// Renders every page of <paramref name="inputPath"/> into <paramref name="folder"/> as <c>&lt;name&gt; 001.jpg</c>…
    /// (<see cref="PdfToImages.PageName"/>), reporting each page as it starts, and returns the number of pages.
    /// </summary>
    /// <param name="inputPath">The PDF; held open, shared for reading only, until the worker has ended.</param>
    /// <param name="folder">An empty folder the caller holds (<see cref="OutputPath.WriteDirectoryAtomicallyAsync(string, string, Func{string, CancellationToken, Task}, CancellationToken)"/>).</param>
    /// <exception cref="PdfException">
    /// The PDF cannot be used (the same errors as <see cref="PdfDocument"/>), needs more memory than the cap
    /// (<see cref="PdfError.NeedsTooMuchMemory"/>), took too long (<see cref="PdfError.TooSlow"/>), or the worker ended
    /// without finishing (<see cref="PdfError.ReaderFailed"/>).
    /// </exception>
    /// <exception cref="IOException">A page could not be written.</exception>
    /// <exception cref="FileNotFoundException">flevert.exe is missing.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the worker has been killed and has exited.</exception>
    public async Task<int> RenderAsync(
        string inputPath, string folder, PdfPageImageFormat format, double dpi, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var (total, run) = await RunAsync(inputPath, input => new PdfRenderRequest(inputPath, input, folder, format, dpi), "render", progress, ct)
            .ConfigureAwait(false);
        // Every page it said it wrote is there under its name; anything else in the folder was put there by someone else.
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        for (var page = 1; page <= total; page++)
        {
            if (!File.Exists(Path.Combine(folder, PdfToImages.PageName(stem, page, total, format))))
                throw new PdfException(PdfError.ReaderFailed, FailedMessage(page, "render"), new PdfWorkerException(run, $"page {page} is missing from the folder"));
        }

        return total;
    }

    /// <summary>
    /// Writes the text of every page of <paramref name="inputPath"/> into the new file <paramref name="outputPath"/>
    /// (<see cref="PdfExtractText"/>), reporting each page as it starts, and returns the number of pages.
    /// </summary>
    /// <param name="outputPath">A path no file has yet: the worker creates it.</param>
    /// <exception cref="OperationSkippedException">The PDF has no text (a scan).</exception>
    /// <exception cref="PdfException">As for <see cref="RenderAsync"/>, and <see cref="PdfError.TooLarge"/> past 100 MB of text.</exception>
    /// <exception cref="IOException">The text could not be written.</exception>
    public async Task<int> ExtractTextAsync(string inputPath, string outputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var (total, run) = await RunAsync(inputPath, input => new PdfRenderRequest(inputPath, input, outputPath, PdfPageImageFormat.Jpeg, 72) { Task = PdfWorkerTask.Text },
            "read", progress, ct).ConfigureAwait(false);
        if (!File.Exists(outputPath))
            throw new PdfException(PdfError.ReaderFailed, FailedMessage(null, "read"), new PdfWorkerException(run, "the text file is missing"));
        return total;
    }

    /// <summary>
    /// Writes into <paramref name="folder"/> a smaller JPEG, <c>&lt;hash&gt;.jpg</c>, of every image of
    /// <paramref name="inputPath"/> drawn above <paramref name="dpi"/> wherever it is drawn (<see cref="PdfCompressSharing"/>),
    /// reporting each page as its images are looked at, and returns them.
    /// </summary>
    /// <param name="folder">An empty folder only this user, SYSTEM and administrators can change.</param>
    /// <exception cref="PdfException">As for <see cref="RenderAsync"/>.</exception>
    /// <exception cref="IOException">An image could not be written.</exception>
    internal async Task<IReadOnlyList<PdfShrunkImage>> ShrinkImagesAsync(
        string inputPath, string folder, double dpi, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        IReadOnlyList<PdfShrunkImage>? shrunk = null;
        var (_, run) = await RunAsync(inputPath, input => new PdfRenderRequest(inputPath, input, folder, PdfPageImageFormat.Jpeg, dpi) { Task = PdfWorkerTask.ShrinkImages },
            "read", progress, ct, session => shrunk = session.Shrunk).ConfigureAwait(false);
        foreach (var image in shrunk!)
        {
            if (!File.Exists(image.Path))
                throw new PdfException(PdfError.ReaderFailed, FailedMessage(null, "read"), new PdfWorkerException(run, $"{image.Hash}.jpg is missing"));
        }

        return shrunk;
    }

    /// <summary>
    /// The pages of <paramref name="inputPath"/> that are blank (<see cref="PdfRemoveBlankPages"/>), numbered from 1, in
    /// order, and the number of pages; each page is reported as it is looked at.
    /// </summary>
    /// <exception cref="PdfException">As for <see cref="RenderAsync"/>.</exception>
    internal async Task<(int Total, IReadOnlyList<int> Blank)> FindBlankPagesAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        IReadOnlyList<int> blank = [];
        // Nothing is written: the folder field names the input's folder, which the worker never opens.
        var (total, _) = await RunAsync(inputPath,
            input => new PdfRenderRequest(inputPath, input, Path.GetDirectoryName(Path.GetFullPath(inputPath))!, PdfPageImageFormat.Jpeg, PdfToolsWorker.BlankDpi)
            {
                Task = PdfWorkerTask.BlankPages,
            },
            "read", progress, ct, session => blank = session.Blank).ConfigureAwait(false);
        return (total, blank);
    }

    /// <summary>
    /// Makes <paramref name="edit"/> to <paramref name="inputPath"/> and saves the result as the new file
    /// <paramref name="outputPath"/>, reporting each page; returns the number of pages of the input.
    /// </summary>
    /// <param name="outputPath">A path no file has yet: the worker creates it.</param>
    /// <exception cref="OperationSkippedException">There is nothing to do (every page is A4 already).</exception>
    /// <exception cref="PdfException">As for <see cref="RenderAsync"/>.</exception>
    /// <exception cref="InvalidOperationException">The worker could not set itself up (the Arial font of Windows is missing).</exception>
    internal async Task<int> EditAsync(string inputPath, string outputPath, PdfEdit edit, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var (total, run) = await RunAsync(inputPath,
            input => new PdfRenderRequest(inputPath, input, outputPath, PdfPageImageFormat.Jpeg, 72) { Task = PdfWorkerTask.Edit, Options = edit.Encode() },
            "read", progress, ct).ConfigureAwait(false);
        if (!File.Exists(outputPath))
            throw new PdfException(PdfError.ReaderFailed, FailedMessage(null, "read"), new PdfWorkerException(run, "the PDF is missing"));
        return total;
    }

    /// <summary>
    /// Writes every image the pages of <paramref name="inputPath"/> draw into <paramref name="folder"/>
    /// (<see cref="PdfExtractImages"/>): <c>&lt;name&gt; 001.jpg</c> or <c>.png</c>, reporting each page as it is looked at
    /// and each image as it is written. Returns the names written, in order.
    /// </summary>
    /// <param name="folder">An empty folder the caller holds (<see cref="OutputPath.WriteDirectoryAtomicallyAsync(string, string, Func{string, CancellationToken, Task}, CancellationToken)"/>).</param>
    /// <exception cref="OperationSkippedException">The pages draw no image.</exception>
    /// <exception cref="PdfException">As for <see cref="RenderAsync"/>.</exception>
    /// <exception cref="IOException">An image could not be written.</exception>
    internal async Task<IReadOnlyList<string>> ExtractImagesAsync(string inputPath, string folder, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        IReadOnlyList<string> kinds = [];
        var (_, run) = await RunAsync(inputPath,
            input => new PdfRenderRequest(inputPath, input, folder, PdfPageImageFormat.Jpeg, 72) { Task = PdfWorkerTask.ExtractImages },
            "read", progress, ct, session => kinds = session.Extracted).ConfigureAwait(false);
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var names = new List<string>(kinds.Count);
        for (var i = 0; i < kinds.Count; i++)
        {
            var name = PdfToImages.PageName(stem, i + 1, kinds.Count, kinds[i] == "jpg" ? PdfPageImageFormat.Jpeg : PdfPageImageFormat.Png);
            if (!File.Exists(Path.Combine(folder, name)))
                throw new PdfException(PdfError.ReaderFailed, FailedMessage(null, "read"), new PdfWorkerException(run, $"{name} is missing from the folder"));
            names.Add(name);
        }

        return names;
    }

    // Runs one worker on the request made for the pinned input; "render" or "read" is what the messages say it was doing.
    // finished: what the request needs from the session, once the worker has done.
    private async Task<(int Total, ExternalToolResult Run)> RunAsync(
        string inputPath, Func<FileIdentity, PdfRenderRequest> makeRequest, string doing, IProgress<FileProgress>? progress, CancellationToken ct,
        Action<Session>? finished = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        RequireExe();

        // Held until the worker has ended, sharing reading only, like the runner's pin: the worker reopens this path, and on
        // Windows nobody can change, rename or replace the file meanwhile; on Linux the path is the runner's pin, which
        // leads to the file it checked whatever its name leads to. The worker checks it opened this very file.
        var readPath = PinnedInput.ReadPath(inputPath);
        using var pin = File.OpenHandle(readPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = RandomAccess.GetLength(pin);
        if (length > PdfDocument.MaxFileBytes)
            throw PdfDocument.TooLargeError(PdfDocument.MaxFileBytes);
        var made = makeRequest(FileIdentity.Of(pin)) with { ReadPath = readPath };
        var request = made.Encode();

        using var session = new Session(this, StallTimeout + (PerHundredMegabytes * (length / 100_000_000.0)), doing, progress, ct)
        {
            ShrinkFolder = made.Task == PdfWorkerTask.ShrinkImages ? made.Folder : null,
            Task = made.Task,
        };
        var limits = new ExternalToolLimits { MaxMemoryBytes = MaxMemoryBytes, SingleProcess = true };
        ExternalToolResult run;
        try
        {
            run = await ExternalTool.RunAsync(ExePath, [Command], RunTimeout, request, limits: limits, environment: WorkerEnvironment(),
                standardOutputLine: session.OnLine, cancellationToken: session.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && session.OverBudget)
        {
            throw new PdfException(PdfError.TooSlow, OverBudgetMessage);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && session.Stalled)
        {
            throw new PdfException(PdfError.TooSlow, session.TooSlowMessage());
        }
        catch (TimeoutException e)
        {
            throw new PdfException(PdfError.TooSlow, session.TooSlowMessage(), e);
        }
        catch (ProtocolException e)
        {
            throw new PdfException(PdfError.ReaderFailed, session.FailedMessage(), e);
        }

        var total = session.Outcome(run, MaxMemoryBytes);
        finished?.Invoke(session);
        return (total, run);
    }

    private static string TooSlowMessage(int? page, string doing) => page is { } p
        ? string.Create(CultureInfo.InvariantCulture, $"Page {p} of this PDF took too long to {doing}: it is too complex, or made to stall PDF readers.")
        : "This PDF took too long to open: it is too complex, or made to stall PDF readers.";

    private static string FailedMessage(int? page, string doing) => page is { } p
        ? string.Create(CultureInfo.InvariantCulture, $"Flevert's PDF reader stopped on page {p} of this PDF without finishing: the PDF may be damaged, or made to crash PDF readers.")
        : "Flevert's PDF reader stopped on this PDF without finishing: it may be damaged, or made to crash PDF readers.";

    /// <summary>
    /// The changes to the environment the worker inherits from flevert.exe (ADR-0008): every <c>MAGICK_*</c> variable,
    /// which steers the ImageMagick inside it (resource limits, temporary folder, where it loads configuration and
    /// modules from; the worker sets its own up), and every <c>FLEVERT_*</c> one, switches for tests, are removed. The
    /// rest stays, .NET's own variables included.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> WorkerEnvironment()
    {
        // Names are case-sensitive on Linux: MAGICK_X and magick_x are two variables, and each is removed.
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in System.Environment.GetEnvironmentVariables().Keys.OfType<string>())
        {
            if (name.StartsWith("MAGICK_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FLEVERT_", StringComparison.OrdinalIgnoreCase))
                environment[name] = null;
        }

        return environment;
    }

    private void RequireExe()
    {
        if (!File.Exists(ExePath))
        {
            throw new FileNotFoundException(
                $"{FileName} is missing from {Path.GetDirectoryName(ExePath)}, where it reads PDFs. Reinstall Flevert.", ExePath);
        }
    }

    /// <summary>What one run of the worker has said so far, and the watchdog that ends it when it stops saying anything.</summary>
    internal sealed class Session : IDisposable
    {
        private readonly PdfWorker _worker;
        private readonly string _doing;
        private readonly IProgress<FileProgress>? _progress;
        private readonly CancellationToken _cancel;
        private readonly CancellationTokenSource _stall = new();
        private readonly CancellationTokenSource _budget = new();
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly CancellationTokenSource _linked;
        private readonly Lock _gate = new();
        private TimeSpan _pageWindow;
        private int _total;
        private int _current;
        private int _written;
        private bool _done;
        private (string Code, string Message)? _error;
        private readonly List<PdfShrunkImage> _shrunk = [];
        private readonly HashSet<string> _shrunkHashes = new(StringComparer.Ordinal);
        private string? _encoding;

        /// <summary>For a request to shrink images: the folder they are written to.</summary>
        public string? ShrinkFolder { get; init; }

        /// <summary>What the worker was asked to do: the lines of the v0.2.2 tools are accepted only for theirs.</summary>
        public PdfWorkerTask Task { get; init; }

        private readonly List<int> _blank = [];
        private int? _imageCount;
        private int _extracted;
        private int _extracting;
        private readonly List<string> _extensions = [];
        private int _savedMegabytes = -1;

        /// <summary>For a request to find blank pages: those the worker said are blank, numbered from 1.</summary>
        public IReadOnlyList<int> Blank
        {
            get
            {
                lock (_gate)
                    return [.. _blank];
            }
        }

        /// <summary>For a request to extract images: "jpg" or "png" for each image written, in order.</summary>
        public IReadOnlyList<string> Extracted
        {
            get
            {
                lock (_gate)
                    return [.. _extensions];
            }
        }

        /// <summary>The most images a PDF may give: 2 000 pages of some each.</summary>
        private const int MaxImages = 1_000_000;

        /// <summary>The images the worker said it wrote, once it is done.</summary>
        public IReadOnlyList<PdfShrunkImage> Shrunk
        {
            get
            {
                lock (_gate)
                    return [.. _shrunk];
            }
        }

        public Session(PdfWorker worker, TimeSpan openWindow, string doing, IProgress<FileProgress>? progress, CancellationToken cancel)
        {
            _worker = worker;
            _doing = doing;
            _progress = progress;
            _cancel = cancel;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _stall.Token, _budget.Token);
            _stall.CancelAfter(openWindow);
        }

        /// <summary>Cancelled by the caller, by the watchdog, or when the whole file is past its budget.</summary>
        public CancellationToken Token => _linked.Token;

        public bool Stalled => _stall.IsCancellationRequested;

        public bool OverBudget => _budget.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _stall.Dispose();
            _budget.Dispose();
        }

        // Set once, when the page count is known: the budget counts from the start of the run.
        private void StartBudget(int pages)
        {
            var budget = _worker.FileBudgetBase + (_worker.FileBudgetPerPage * pages);
            var left = budget - _elapsed.Elapsed;
            _budget.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }

        /// <summary>
        /// One line from the worker; lines come one at a time, in order. A line that breaks the protocol ends the run (the
        /// worker is killed). The page goes to the progress window outside the lock, which only guards the counts.
        /// </summary>
        public void OnLine(string line)
        {
            // Nothing more once cancelled: the window must not show a page after "Cancelling…".
            if (Read(line) is { } page && !_cancel.IsCancellationRequested)
                _progress?.Report(page);
        }

        private FileProgress? Read(string line)
        {
            var parts = line.Split(' ', 3);
            lock (_gate)
            {
                switch (parts[0])
                {
                    case "pages" when parts.Length == 2 && _total == 0:
                        _total = Number(parts[1], 1, PdfDocument.MaxPages);
                        StartBudget(_total);
                        Watch(_worker.StallTimeout);
                        return null;
                    case "page" when parts.Length == 3 && _total > 0:
                        var page = Number(parts[1], _current + 1, _current + 1);
                        if (page > _total || _written != _current)
                            throw new ProtocolException(line);
                        var size = parts[2].Split(' ');
                        if (size.Length != 2)
                            throw new ProtocolException(line);
                        _pageWindow = _worker.StallWindow(
                            Number(size[0], 1, PdfDocument.MaxRenderPixels), Number(size[1], 1, PdfDocument.MaxRenderPixels));
                        _current = page;
                        Watch(_pageWindow);
                        return new FileProgress("page", page, _total);
                    case "busy" when parts.Length == 2 && _current > _written:
                        Number(parts[1], _current, _current);
                        Watch(_pageWindow);
                        return null;
                    case "wrote" when parts.Length == 2 && _current > _written:
                        _written = Number(parts[1], _current, _current);
                        Watch(_worker.StallTimeout);
                        return null;
                    case "encode" when ShrinkFolder is not null && parts.Length == 3 && _total > 0 && _written == _total && _encoding is null:
                        var encode = parts[2].Split(' ');
                        if (encode.Length != 2 || !IsHash(parts[1]) || _shrunkHashes.Contains(parts[1]))
                            throw new ProtocolException(line);
                        var (width, height) = ImageSize(encode[0], encode[1], line);
                        _encoding = parts[1];
                        // Decoding and encoding grow with the pixels, as drawing a page does.
                        Watch(_worker.StallWindow(width, height));
                        return null;
                    case "image" when _encoding is not null && parts.Length == 3 && parts[1] == _encoding:
                        var image = parts[2].Split(' ');
                        if (image.Length != 3 || image[2] is not ("gray" or "rgb"))
                            throw new ProtocolException(line);
                        var (shrunkWidth, shrunkHeight) = ImageSize(image[0], image[1], line);
                        _shrunk.Add(new PdfShrunkImage(parts[1], Path.Combine(ShrinkFolder!, parts[1] + ".jpg"), shrunkWidth, shrunkHeight, image[2] == "gray"));
                        _shrunkHashes.Add(parts[1]);
                        _encoding = null;
                        Watch(_worker.StallTimeout);
                        return null;
                    case "kept" when _encoding is not null && parts.Length == 2 && parts[1] == _encoding:
                        _shrunkHashes.Add(parts[1]);
                        _encoding = null;
                        Watch(_worker.StallTimeout);
                        return null;
                    case "blank" when Task == PdfWorkerTask.BlankPages && parts.Length == 2 && _current > _written
                        && (_blank.Count == 0 || _blank[^1] != _current):
                        _blank.Add(Number(parts[1], _current, _current));
                        return null;
                    case "saved" when Task == PdfWorkerTask.Edit && parts.Length == 2 && _total > 0 && _written == _total:
                        _savedMegabytes = Number(parts[1], _savedMegabytes + 1, int.MaxValue);
                        Watch(_worker.StallTimeout);
                        return null;
                    case "images" when Task == PdfWorkerTask.ExtractImages && parts.Length == 2 && _total > 0 && _written == _total && _imageCount is null:
                        _imageCount = Number(parts[1], 0, MaxImages);
                        Watch(_worker.StallTimeout);
                        return null;
                    case "extract" when _imageCount is { } count && parts.Length == 3 && _extracting == 0 && _extracted < count:
                        _extracting = Number(parts[1], _extracted + 1, _extracted + 1);
                        var extract = parts[2].Split(' ');
                        if (extract.Length != 2)
                            throw new ProtocolException(line);
                        // A JPEG is copied, not decoded: its size only sets the watchdog's window, capped like a page.
                        var (extractWidth, extractHeight) = ImageSize(extract[0], extract[1], line, long.MaxValue);
                        Watch(_worker.StallWindow(Math.Min(extractWidth, PdfDocument.MaxRenderPixels), Math.Min(extractHeight, PdfDocument.MaxRenderPixels)));
                        return new FileProgress("image", _extracting, count);
                    case "extracted" when _extracting > 0 && parts.Length == 3 && parts[2] is "jpg" or "png":
                        Number(parts[1], _extracting, _extracting);
                        _extensions.Add(parts[2]);
                        (_extracted, _extracting) = (_extracting, 0);
                        Watch(_worker.StallTimeout);
                        return null;
                    case "done" when parts.Length == 2 && _total > 0 && _written == _total && _encoding is null
                        && (Task != PdfWorkerTask.ExtractImages || (_imageCount == _extracted && _extracting == 0)):
                        Number(parts[1], _total, _total);
                        _done = true;
                        _stall.CancelAfter(Timeout.InfiniteTimeSpan);
                        _budget.CancelAfter(Timeout.InfiniteTimeSpan);
                        return null;
                    case "error" when parts.Length == 3 && _error is null:
                        _error = (parts[1], PdfRenderWorker.OneLine(parts[2]));
                        Watch(_worker.StallTimeout); // it is about to exit
                        return null;
                    case "pages" or "page" or "busy" or "wrote" or "done" or "error" or "encode" or "image" or "kept"
                        or "blank" or "saved" or "images" or "extract" or "extracted":
                        throw new ProtocolException(line);
                    default:
                        // Not the worker's: a native library writing to standard output. It proves nothing, so it does
                        // not hold off the watchdog either.
                        return null;
                }
            }
        }

        private void Watch(TimeSpan window) => _stall.CancelAfter(window);

        // The pixel size of an image the worker decodes or wrote: at most MaxRenderPixels² in all, the most it decodes.
        private static (int Width, int Height) ImageSize(string width, string height, string line) =>
            ImageSize(width, height, line, PdfDocument.MaxRenderPixels * PdfDocument.MaxRenderPixels);

        private static (int Width, int Height) ImageSize(string width, string height, string line, long maxPixels)
        {
            var side = (int)Math.Min(maxPixels, int.MaxValue);
            var size = (Width: Number(width, 1, side), Height: Number(height, 1, side));
            return (long)size.Width * size.Height <= maxPixels ? size : throw new ProtocolException(line);
        }

        // A SHA-256 as the worker writes it: 64 lower-case hexadecimal digits, which is also a safe file name.
        private static bool IsHash(string text) => text.Length == 64 && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

        private static int Number(string text, int min, int max) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
                ? value
                : throw new ProtocolException(text);

        /// <summary>The page count when the worker finished; otherwise the error its end means.</summary>
        public int Outcome(ExternalToolResult run, ulong maxMemoryBytes)
        {
            lock (_gate)
            {
                // A refused allocation can make pdfium end the process at once, without a word: the job's report says why.
                if (run.MemoryLimitExceeded || _error?.Code == nameof(PdfError.NeedsTooMuchMemory))
                {
                    throw new PdfException(PdfError.NeedsTooMuchMemory, Qpdf.NeedsTooMuchMemoryMessage(maxMemoryBytes),
                        new PdfWorkerException(run, _error?.Message));
                }

                if (_error is var (code, message))
                {
                    if (code == "Write")
                        throw new IOException(message, new PdfWorkerException(run, null));
                    if (code == "Setup")
                        throw new InvalidOperationException(message, new PdfWorkerException(run, null));
                    if (code == nameof(PdfError.PasswordRequired))
                        throw new PdfException(PdfError.PasswordRequired, PdfException.NeedsPasswordMessage);
                    if (code == PdfRenderWorker.NoTextCode)
                        throw new OperationSkippedException(PdfRenderWorker.NoTextMessage);
                    if (code == PdfRenderWorker.SkippedCode)
                        throw new OperationSkippedException(message);
                    if (Enum.TryParse<PdfError>(code, ignoreCase: false, out var error) && Enum.IsDefined(error) && !code.Any(char.IsDigit))
                        throw new PdfException(error, message, new PdfWorkerException(run, null));
                }

                if (run.ExitCode == PdfRenderWorker.ExitDone && _done && _error is null)
                    return _total;
                throw new PdfException(PdfError.ReaderFailed, FailedMessage(), new PdfWorkerException(run, null));
            }
        }

        // The page the worker was on: the one it announced and has not written, or the next one, whose size it reads
        // before announcing it.
        private int? PageAt()
        {
            if (_total == 0)
                return null;
            return Math.Min(_written < _current ? _current : _current + 1, _total);
        }

        public string TooSlowMessage()
        {
            lock (_gate)
                return PdfWorker.TooSlowMessage(PageAt(), _doing);
        }

        public string FailedMessage()
        {
            lock (_gate)
                return PdfWorker.FailedMessage(PageAt(), _doing);
        }
    }

    /// <summary>A line from the worker that does not follow the protocol (<see cref="PdfRenderWorker"/>).</summary>
    internal sealed class ProtocolException(string line) : Exception($"The PDF worker wrote an unexpected line: {PdfRenderWorker.OneLine(line)}");
}

/// <summary>How the PDF worker ended, for the log: its exit code and the end of what it wrote to standard error.</summary>
public sealed class PdfWorkerException : Exception
{
    private const int MaxDetailChars = 2_000;

    public PdfWorkerException() { }

    public PdfWorkerException(string message) : base(message) { }

    public PdfWorkerException(string message, Exception innerException) : base(message, innerException) { }

    internal PdfWorkerException(ExternalToolResult run, string? detail)
        : base(Describe(run, detail)) => ExitCode = run.ExitCode;

    public int ExitCode { get; }

    private static string Describe(ExternalToolResult run, string? detail)
    {
        var error = run.StandardError.Trim();
        if (error.Length > MaxDetailChars)
            error = "…" + error[^MaxDetailChars..];
        var text = string.Create(CultureInfo.InvariantCulture, $"The PDF worker ended with exit code {run.ExitCode} (0x{run.ExitCode:X8})");
        if (run.MemoryLimitExceeded)
            text += OperatingSystem.IsWindows() ? ", after Windows refused it memory past its limit" : ", after it was refused memory past its limit";
        if (detail is not null)
            text += $": {detail}";
        return error.Length == 0 ? text + "." : $"{text}. {error}";
    }
}
