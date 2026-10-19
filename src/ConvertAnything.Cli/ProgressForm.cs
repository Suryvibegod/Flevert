using ConvertAnything.Core;

namespace ConvertAnything.Cli;

internal sealed class ProgressForm : Form
{
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 40, AutoEllipsis = true };
    // Named for screen readers, which otherwise announce only "progress bar"; Alt+C and Esc both cancel.
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18, AccessibleName = "Conversion progress" };
    private readonly Button _cancel = new() { Text = "&Cancel", Anchor = AnchorStyles.Right | AnchorStyles.Bottom, AutoSize = true };

    public ProgressForm(string title, int total, CancellationTokenSource cancellation)
    {
        Text = $"ConvertAnything — {title}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 110);
        Padding = new Padding(12);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        _bar.Maximum = Math.Max(total, 1);
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _status.Text = "Cancelling…";
            cancellation.Cancel();
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = "Actions",
        };
        buttons.Controls.Add(_cancel);
        Controls.Add(buttons);
        Controls.Add(_bar);
        Controls.Add(_status);
        CancelButton = _cancel;
    }

    /// <summary>Set before the batch closes the window itself, so only a user close counts as Cancel.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Finishing { get; set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!Finishing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // keep the window until the current file stops
            _cancel.PerformClick();
            return;
        }

        base.OnFormClosing(e);
    }

    private string? _lastStarted;

    public void Report(BatchProgress progress)
    {
        _bar.Maximum = Math.Max(progress.Total, 1); // grows when late files join the batch
        _bar.Value = Math.Min(progress.Completed, _bar.Maximum);
        if (_cancel.Enabled)
            _status.Text = BatchProgressText.Describe(progress, _lastStarted);
        _lastStarted = progress.Current ?? _lastStarted;
    }
}
