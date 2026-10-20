using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>What the user chose in the PDF tools window: a tool and the values of its options.</summary>
public sealed record PdfToolChoice(PdfTool Tool, IReadOnlyDictionary<string, string> Options);

/// <summary>Asks the user which PDF tool to run: the window of flevert.exe, or a fake in tests.</summary>
public interface IPdfToolPrompt
{
    /// <returns>The choice, or <c>null</c> when the user chose Cancel.</returns>
    /// <exception cref="InvalidOperationException">There is no way to ask (flevert.exe runs without windows).</exception>
    Task<PdfToolChoice?> AskAsync(CancellationToken ct);
}

/// <summary>The prompt that needs no window.</summary>
public static class PdfToolPrompts
{
    public const string NoWindowMessage =
        "Flevert shows the PDF tools only in a window, and it runs without windows here (FLEVERT_HEADLESS), so this PDF was left as it is. Run a tool by its id instead, such as flevert.exe run pdf.remove-blank-pages.";

    public const string CancelledMessage = "No PDF tool was chosen.";

    /// <summary>Refuses at once: nothing may wait for an answer no one can give, as for passwords.</summary>
    public static IPdfToolPrompt NoWindow { get; } = new NoWindowPrompt();

    private sealed class NoWindowPrompt : IPdfToolPrompt
    {
        public Task<PdfToolChoice?> AskAsync(CancellationToken ct) =>
            Task.FromException<PdfToolChoice?>(new InvalidOperationException(NoWindowMessage));
    }
}

/// <summary>
/// <c>More PDF tools…</c> (docs/specs/pdf.md, v0.2.2): asks once for the whole batch which tool to run, in the PDF tools
/// window, then runs that tool on every file of the batch. Cancel changes nothing: each file is skipped (flevert.exe also
/// ends the batch without a notification).
/// </summary>
/// <remarks>
/// The files of the batch wait for the one answer; the first one to arrive asks. After it, at most as many files run at
/// once as the chosen tool allows (its <see cref="IOperation.MaxParallel"/>), whatever this operation told the runner.
/// </remarks>
public sealed class PdfToolsOperation : IOperation, IDisposable
{
    private readonly IPdfToolPrompt _prompt;
    private readonly Func<PdfToolChoice, IOperation> _create;
    private readonly Lock _gate = new();
    private Task<Chosen?>? _chosen;

    /// <param name="prompt">Asked which tool to run; without one, every file fails with
    /// <see cref="PdfToolPrompts.NoWindowMessage"/>, as flevert.exe without windows does.</param>
    /// <param name="create">Makes the operation for the tool and options chosen.</param>
    public PdfToolsOperation(Func<PdfToolChoice, IOperation> create, IPdfToolPrompt? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        _prompt = prompt ?? PdfToolPrompts.NoWindow;
    }

    public const string OperationId = "pdf.tools";

    public string Id => OperationId;

    public string MenuPath => "More PDF tools…";

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    // How many files may wait for the answer at once, pinned; the chosen tool's own limit applies after it.
    public int MaxParallel => 4;

    /// <summary>A copy that asks <paramref name="prompt"/>, once for the batch. Dispose it when the batch ends.</summary>
    public PdfToolsOperation WithPrompt(IPdfToolPrompt prompt) => new(_create, prompt);

    public Task<string> RunAsync(string inputPath, CancellationToken ct) => RunAsync(inputPath, null, ct);

    public async Task<string> RunAsync(string inputPath, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var chosen = await ChooseAsync(ct).WaitAsync(ct).ConfigureAwait(false)
            ?? throw new OperationSkippedException(PdfToolPrompts.CancelledMessage);
        await chosen.Slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await chosen.Operation.RunAsync(inputPath, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            chosen.Slots.Release();
        }
    }

    // One question for the batch: every file awaits the same answer, also an error (no window), which fails each of them.
    private Task<Chosen?> ChooseAsync(CancellationToken ct)
    {
        lock (_gate)
            return _chosen ??= AskAsync(ct);
    }

    private async Task<Chosen?> AskAsync(CancellationToken ct)
    {
        var choice = await _prompt.AskAsync(ct).ConfigureAwait(false);
        if (choice is null)
            return null;
        var operation = _create(choice);
        return new Chosen(operation, new SemaphoreSlim(Math.Max(operation.MaxParallel, 1)));
    }

    public void Dispose()
    {
        Task<Chosen?>? chosen;
        lock (_gate)
            chosen = _chosen;
        if (chosen is { IsCompletedSuccessfully: true, Result: { } done })
        {
            (done.Operation as IDisposable)?.Dispose();
            done.Slots.Dispose();
        }
    }

    private sealed record Chosen(IOperation Operation, SemaphoreSlim Slots);
}
