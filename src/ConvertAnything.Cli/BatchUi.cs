using System.Diagnostics;
using ConvertAnything.Core;

namespace ConvertAnything.Cli;

/// <summary>Runs a batch with a progress window for slow batches and a single notification at the end.</summary>
internal sealed class BatchUi : ApplicationContext
{
    private static readonly TimeSpan ShowProgressAfter = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NotificationLifetime = TimeSpan.FromSeconds(6);

    private readonly CancellationTokenSource _cancellation = new();
    private readonly ProgressForm _form;
    private readonly RunLog _log;
    private NotifyIcon? _tray;

    private BatchUi(IOperation operation, IReadOnlyList<string> files, RunLog log)
    {
        _log = log;
        _form = new ProgressForm(operation.MenuPath.Replace('/', ' '), files.Count, _cancellation);
        _ = _form.Handle; // installs the WinForms synchronization context so Progress<T> reports on this thread

        var progress = new Progress<BatchProgress>(_form.Report);
        var work = Task.Run(() => new Runner(log).RunAsync(operation, files, progress, _cancellation.Token));

        var reveal = new System.Windows.Forms.Timer { Interval = (int)ShowProgressAfter.TotalMilliseconds };
        reveal.Tick += (_, _) =>
        {
            reveal.Dispose();
            if (!work.IsCompleted)
                _form.Show();
        };
        reveal.Start();

        work.ContinueWith(t => Finish(t.Result, files.Count), TaskScheduler.FromCurrentSynchronizationContext());
    }

    public bool AllSucceeded { get; private set; }

    public static bool Run(IOperation operation, IReadOnlyList<string> files, RunLog log)
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var ui = new BatchUi(operation, files, log);
        Application.Run(ui);
        return ui.AllSucceeded;
    }

    private void Finish(IReadOnlyList<FileResult> results, int total)
    {
        AllSucceeded = results.Count == total && results.All(r => r.Succeeded);
        _form.Close();

        var summary = BatchSummary.Describe(results, total);
        _tray = new NotifyIcon { Icon = _form.Icon, Text = "ConvertAnything", Visible = true };
        if (summary.HasFailures)
            _tray.BalloonTipClicked += (_, _) => OpenLog();
        _tray.BalloonTipClosed += (_, _) => ExitThread();
        _tray.ShowBalloonTip((int)NotificationLifetime.TotalMilliseconds, "ConvertAnything", summary.Text,
            summary.HasFailures ? ToolTipIcon.Warning : ToolTipIcon.Info);

        // BalloonTipClosed is not raised when the system suppresses the balloon (e.g. Focus Assist), so also exit on a timer.
        var exit = new System.Windows.Forms.Timer { Interval = (int)NotificationLifetime.TotalMilliseconds };
        exit.Tick += (_, _) => ExitThread();
        exit.Start();
    }

    private void OpenLog()
    {
        if (File.Exists(_log.CurrentFile))
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { $"/select,{_log.CurrentFile}" } });
    }

    protected override void ExitThreadCore()
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _form.Dispose();
            _cancellation.Dispose();
        }

        base.Dispose(disposing);
    }
}
