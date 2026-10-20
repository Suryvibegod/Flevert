using Flevert.Core;
using Flevert.Core.Menu;

namespace Flevert.Pdf;

/// <summary>
/// The second menu of a PDF, <c>Flevert PDF tools</c> (docs/specs/pdf.md, v0.2.2): the tools of <see cref="PdfTools"/>
/// placed in it, each run straight from the menu with its default options. Only Add watermark… asks something: the text,
/// in a dialog like the password one (<see cref="PdfWatermarkMenu"/>).
/// </summary>
public static class PdfToolMenu
{
    /// <summary>Add page numbers from the menu: at the bottom centre, as "1 / 12".</summary>
    public static IReadOnlyDictionary<string, string> PageNumbers { get; } =
        new Dictionary<string, string> { [PdfTools.Position] = PdfTools.BottomCenter, [PdfTools.Style] = PdfTools.OfTotalStyle };

    /// <summary>In menu order: 12 entries, submenus counted.</summary>
    public static IReadOnlyList<IMenuOperation> All { get; } =
    [
        Placed("pdf.remove-blank-pages", "Remove blank pages"),
        Placed("pdf.page-numbers", "Add page numbers", PageNumbers),
        new PdfWatermarkMenu(),
        Placed("pdf.n-up-2", "Print layout/2 pages per sheet"),
        Placed("pdf.n-up-4", "Print layout/4 pages per sheet"),
        Placed("pdf.fit-a4", "Fit pages to A4"),
        Placed("pdf.flatten", "Flatten forms and comments"),
        Placed("pdf.split-10mb", "Split by size/Under 10 MB"),
        Placed("pdf.split-25mb", "Split by size/Under 25 MB"),
        Placed("pdf.extract-images", "Extract images"),
    ];

    private static PlacedOperation Placed(string operationId, string menuPath, IReadOnlyDictionary<string, string>? options = null) =>
        new(PdfTools.Create(operationId, options ?? new Dictionary<string, string>()), menuPath, MenuCascade.PdfTools);
}

/// <summary>An operation shown at a place of the menu it does not name itself: a PDF tool, which has no menu path.</summary>
public sealed class PlacedOperation(IOperation inner, string menuPath, MenuCascade cascade) : IOperation, IMenuPlacement
{
    public string Id => inner.Id;

    public string MenuPath => menuPath;

    public MenuCascade Cascade => cascade;

    public IReadOnlyCollection<string> Extensions => inner.Extensions;

    public int MaxParallel => inner.MaxParallel;

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => inner.RunAsync(inputPath, ct);

    public Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct) =>
        inner.RunAsync(inputPath, progress, ct);
}

/// <param name="InputPath">The PDF the text is for.</param>
/// <param name="AnsweredInProgress">Files of this batch that already have their text and are still being worked on; the
/// dialog leaves them out of the count it offers the text for ("Use this text for all 4 files").</param>
public sealed record WatermarkQuestion(string InputPath, int AnsweredInProgress);

/// <param name="Text">The text, or <c>null</c> when the user chose Cancel.</param>
/// <param name="Files">How many files of the batch the answer is for, the one asked about included.</param>
public sealed record WatermarkAnswer(string? Text, int Files);

/// <summary>Asks the user for the watermark text: the dialog of flevert.exe, or a fake in tests.</summary>
public interface IWatermarkPrompt
{
    /// <exception cref="InvalidOperationException">There is no way to ask (flevert.exe runs without windows).</exception>
    Task<WatermarkAnswer> AskAsync(WatermarkQuestion question, CancellationToken ct);
}

/// <summary>The checks the watermark dialog makes before it lets OK through, and the prompt that needs no window.</summary>
public static class WatermarkPrompts
{
    public const string NoWindowMessage =
        "Flevert asks for the watermark text only in a window, and it runs without windows here (FLEVERT_HEADLESS), so this PDF was left as it is.";

    public const string NoTextMessage = "No watermark text was given.";
    public const string EmptyMessage = "Type the text to write on the pages.";
    public const string NotOneLineMessage = "The text must be one line.";
    public const string InvisibleMessage =
        "The text has an invisible character (such as a direction mark or a zero-width space), which could make the watermark read other than it shows: type it again without it.";

    /// <summary>The most characters the text holds (the tool's own limit), counted as people count them.</summary>
    public static int MaxLength { get; } =
        PdfTools.All.Single(t => t.OperationId == PdfWatermarkMenu.OperationId).Options.Single(o => o.Key == PdfTools.Text).MaxLength;

    /// <summary>What the text box lets the user type: room for letters typed as several code points (e with two marks
    /// is three), so the box never cuts a letter or an emoji in half; <see cref="Problem"/> counts.</summary>
    public static int FieldMaxLength { get; } = 4 * MaxLength;

    public static string TooLongMessage { get; } = $"The text can be at most {MaxLength} characters.";

    /// <summary>The reason for characters Windows' Arial, the watermark's font, has no glyph for.</summary>
    public static string NoGlyphMessage(string characters) =>
        $"The watermark is written in Arial, which cannot draw {characters}: leave {(characters.Contains(',', StringComparison.Ordinal) ? "them" : "it")} out.";

    /// <summary>Refuses every question, at once: nothing may wait for an answer no one can give.</summary>
    public static IWatermarkPrompt NoWindow { get; } = new NoWindowPrompt();

    /// <summary>
    /// Why the text cannot be used, or <c>null</c> when it can: the watermark tool's own rule (<see cref="PdfEdit.Check"/>:
    /// one line, no invisible character, at most <see cref="MaxLength"/> characters as people count them), and every
    /// character one Arial can draw.
    /// </summary>
    public static string? Problem(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PdfEdit.Check(text, out var normal) switch
        {
            WatermarkTextProblem.Empty => EmptyMessage,
            WatermarkTextProblem.NotOneLine => NotOneLineMessage,
            WatermarkTextProblem.Invisible => InvisibleMessage,
            WatermarkTextProblem.TooLong => TooLongMessage,
            _ => FontGlyphs.Arial?.Missing(normal) is { Count: > 0 } missing ? NoGlyphMessage(string.Join(", ", missing)) : null,
        };
    }

    private sealed class NoWindowPrompt : IWatermarkPrompt
    {
        public Task<WatermarkAnswer> AskAsync(WatermarkQuestion question, CancellationToken ct) =>
            Task.FromException<WatermarkAnswer>(new InvalidOperationException(NoWindowMessage));
    }
}

/// <summary>
/// The watermark texts of one batch, as <see cref="BatchPasswords"/> for passwords: one question at a time, and an answer
/// given for N files serves the next N − 1 files without asking again.
/// </summary>
#pragma warning disable CA1001 // Like BatchPasswords: the semaphore has no wait handle, and a file still waiting when the batch ends must not throw from Release.
public sealed class BatchWatermarks(IWatermarkPrompt prompt)
#pragma warning restore CA1001
{
    private readonly SemaphoreSlim _asking = new(1, 1);
    private readonly Lock _gate = new();
    private string? _shared;
    private int _remaining;
    private int _inUse;

    /// <summary>The text for <paramref name="inputPath"/>, or <c>null</c> when the user chose Cancel for it. Call
    /// <see cref="Done"/> when the file is finished.</summary>
    public async Task<string?> GetAsync(string inputPath, CancellationToken ct)
    {
        await _asking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int inUse;
            lock (_gate)
            {
                if (_remaining > 0)
                {
                    _remaining--;
                    return Take(_shared);
                }

                inUse = _inUse;
            }

            var answer = await prompt.AskAsync(new WatermarkQuestion(inputPath, inUse), ct).ConfigureAwait(false);
            var text = answer.Text?.Trim();
            if (text is not null && WatermarkPrompts.Problem(text) is { } problem)
                throw new ArgumentException(problem, nameof(inputPath));
            lock (_gate)
            {
                _shared = text;
                _remaining = Math.Max(answer.Files, 1) - 1;
                return Take(text);
            }
        }
        finally
        {
            _asking.Release();
        }
    }

    /// <summary>A file that got a text is finished.</summary>
    public void Done()
    {
        lock (_gate)
            _inUse--;
    }

    // Under _gate.
    private string? Take(string? text)
    {
        if (text is not null)
            _inUse++;
        return text;
    }
}

/// <summary>
/// <c>Add watermark…</c> in Flevert PDF tools: asks for the text, once for the batch when the user agrees, then writes it
/// on every page with the watermark tool (<c>&lt;name&gt; (watermarked).pdf</c>). Cancel skips the file. Without a window
/// (FLEVERT_HEADLESS) each file fails at once, saying why.
/// </summary>
public sealed class PdfWatermarkMenu : IOperation, IMenuPlacement
{
    public const string OperationId = "pdf.watermark";

    private readonly IOperation _defaults = PdfTools.Create(OperationId, new Dictionary<string, string>());
    private readonly BatchWatermarks _texts;
    private readonly Func<string, IOperation> _create;

    /// <param name="prompt">Asked for the text; without one, every file fails with <see cref="WatermarkPrompts.NoWindowMessage"/>.</param>
    /// <param name="create">Makes the watermark operation for a text; the real tool unless a test says otherwise.</param>
    public PdfWatermarkMenu(IWatermarkPrompt? prompt = null, Func<string, IOperation>? create = null)
    {
        _texts = new BatchWatermarks(prompt ?? WatermarkPrompts.NoWindow);
        _create = create ?? (text => PdfTools.Create(OperationId, new Dictionary<string, string> { [PdfTools.Text] = text }));
    }

    public string Id => OperationId;

    public string MenuPath => "Add watermark…";

    public MenuCascade Cascade => MenuCascade.PdfTools;

    public IReadOnlyCollection<string> Extensions => _defaults.Extensions;

    public int MaxParallel => _defaults.MaxParallel;

    /// <summary>A copy that asks <paramref name="prompt"/>, once per batch where the user agrees.</summary>
    public PdfWatermarkMenu WithPrompt(IWatermarkPrompt prompt) => new(prompt, _create);

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var text = await _texts.GetAsync(inputPath, ct).ConfigureAwait(false)
            ?? throw new OperationSkippedException(WatermarkPrompts.NoTextMessage);
        try
        {
            return await _create(text).RunAsync(inputPath, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _texts.Done();
        }
    }
}
