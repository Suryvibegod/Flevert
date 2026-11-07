using System.Diagnostics;

namespace Flevert.Pdf.Tests;

/// <summary>
/// qpdf next to the app, cancelling as soon as it starts with an option of the step that writes the output, not the
/// page count before it: the cancel always comes while that qpdf runs, however fast the machine. A timer did not (qpdf
/// on the Linux runners finished a merge within 500 ms, and on Windows the timer went off in the page count).
/// </summary>
internal sealed class CancelWhenQpdfRuns : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Stopwatch _sinceCancel = new();
    private int? _processId;

    /// <param name="option">An option, or the start of one (<c>--rotate=</c>), that only the step to cancel is given.</param>
    public CancelWhenQpdfRuns(string option)
    {
        Qpdf = new Qpdf(Qpdf.NextToApp.ExePath)
        {
            AfterStart = process =>
            {
                if (_processId is not null || !process.StartInfo.ArgumentList.Any(a => a.StartsWith(option, StringComparison.Ordinal)))
                    return;
                _processId = process.Id;
                _sinceCancel.Start();
                _cancel.Cancel();
            },
        };
    }

    public Qpdf Qpdf { get; }

    public CancellationToken Token => _cancel.Token;

    /// <summary>The step's qpdf was cancelled, ended soon after rather than running on, and is gone.</summary>
    public void AssertStopped()
    {
        Assert.True(_processId is not null, "the step to cancel never ran qpdf");
        Assert.True(_sinceCancel.Elapsed < TimeSpan.FromSeconds(3), $"took {_sinceCancel.Elapsed} after the cancel");
        Assert.False(TestProcesses.IsRunningChild(_processId.Value), $"qpdf {_processId} still runs");
    }

    public void Dispose() => _cancel.Dispose();
}
