using Flevert.Core;

namespace Flevert.Cli;

/// <summary>
/// A question a batch asks in a window, on the batch's UI thread, while the batch waits for the answer: a PDF password,
/// or which PDF tool to run. <see cref="BatchUi"/> attaches it once its own window exists.
/// </summary>
internal interface IBatchQuestion
{
    void Attach(BatchQuestionHost host);

    /// <summary>The batch's progress, on the UI thread: how many files an answer could be for.</summary>
    void Report(BatchProgress progress);
}

/// <param name="Ui">The batch's UI thread.</param>
/// <param name="Owner">The progress window when it is on screen, so the question stays above it.</param>
/// <param name="Asking">Told when a question opens and closes: the progress window waits for the answer.</param>
/// <param name="EndQuietly">Ends the batch with nothing done and no notification: the user cancelled the question
/// itself, which is not an error (the PDF tools window).</param>
/// <param name="Retitle">Names the progress window after what the answer chose ("Flevert — Add page numbers").</param>
internal sealed record BatchQuestionHost(
    SynchronizationContext Ui, Func<IWin32Window?> Owner, Action<bool> Asking, Action EndQuietly, Action<string> Retitle);
