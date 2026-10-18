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
    private readonly string _logFile; // fixed at batch start so a batch crossing midnight opens its own log
    private NotifyIcon? _tray;

    private BatchUi(IOperation operation, BatchLeader leader, RunLog log)
    {
        _logFile = log.CurrentFile;
        _form = new ProgressForm(operation.MenuPath.Replace('/', ' '), leader.Pending, _cancellation);
        _ = _form.Handle; // installs the WinForms synchronization context so Progress<T> reports on this thread

        var progress = new Progress<BatchProgress>(_form.Report);
        var work = Task.Run(async () =>
        {
            try
            {
                return await new Runner(log).RunAsync(
                    operation, leader.ReadAllAsync(_cancellation.Token), () => leader.Pending, progress, _cancellation.Token);
            }
#pragma warning disable CA1031 // Whatever happens, the batch must end with a notification and the process must exit.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                log.Write(operation.Id, new FileResult("(batch)", null, ex.ToString()), TimeSpan.Zero);
                return new BatchOutcome([new FileResult("(batch)", null, "batch failed")], 1);
            }
        });

        var reveal = new System.Windows.Forms.Timer { Interval = (int)ShowProgressAfter.TotalMilliseconds };
        reveal.Tick += (_, _) =>
        {
            reveal.Dispose();
            if (!work.IsCompleted)
                _form.Show();
        };
        reveal.Start();

        work.ContinueWith(t => Finish(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
    }

    public bool AllSucceeded { get; private set; }

    public static bool Run(IOperation operation, BatchLeader leader, RunLog log)
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var ui = new BatchUi(operation, leader, log);
        Application.Run(ui);
        return ui.AllSucceeded;
    }

    private void Finish(BatchOutcome outcome)
    {
        AllSucceeded = outcome.Results.Count == outcome.Total && outcome.Results.All(r => r.Succeeded);
        _form.Close();

        var summary = BatchSummary.Describe(outcome.Results, outcome.Total);
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
        if (File.Exists(_logFile))
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            {
                ArgumentList = { $"/select,{_logFile}" },
            });
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
