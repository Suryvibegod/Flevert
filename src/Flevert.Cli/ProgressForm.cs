using Flevert.Core;

namespace Flevert.Cli;

internal sealed class ProgressForm : Form
{
    // File names are shown as they are: with mnemonics on, "R&D.jpg" would read "RD.jpg" with an underlined D.
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 40, AutoEllipsis = true, UseMnemonic = false };
    // "page 37 of 200" for the file named above, when its operation reports it; always there, empty otherwise, so the
    // window does not jump. Not a live region: a screen reader announcing 200 pages would drown out everything else.
    private readonly Label _detail = new() { AutoSize = false, Dock = DockStyle.Top, Height = 22, AutoEllipsis = true, UseMnemonic = false };
    // Named for screen readers, which otherwise announce only "progress bar".
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18, AccessibleName = "Conversion progress" };
    // No access key: on a focused button a bare letter fires it, so typing "c" would cancel the batch. Esc cancels.
    private readonly Button _cancel = new() { Text = "Cancel", Anchor = AnchorStyles.Right | AnchorStyles.Bottom, AutoSize = true };

    public ProgressForm(string title, int total, CancellationTokenSource cancellation)
    {
        Text = $"Flevert — {title}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 132);
        Padding = new Padding(12);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        _bar.Maximum = BatchProgressText.Bar(new BatchProgress(0, total, null)).Maximum;
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _status.Text = "Cancelling…";
            ShowDetail(null);
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
        Controls.Add(_detail); // docked top in reverse order: status, then detail, then the bar
        Controls.Add(_status);
        ShowDetail(null);
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
        // Grows when late files join the batch; moves within a file that reports its pages.
        var (value, maximum) = BatchProgressText.Bar(progress);
        _bar.Maximum = maximum;
        _bar.Value = value;
        if (_cancel.Enabled)
        {
            _status.Text = BatchProgressText.Describe(progress, _lastStarted);
            // A file starting or finishing reports no page: the line clears until the next file reports one.
            ShowDetail(BatchProgressText.DescribeWithin(progress));
        }

        _lastStarted = progress.Current ?? _lastStarted;
    }

    // The name carries the text with its context, so a screen reader reading the window says what the count is about.
    private void ShowDetail(string? text)
    {
        _detail.Text = text ?? "";
        _detail.AccessibleName = text is null ? "Progress in the current file: none reported" : $"Progress in the current file: {text}";
    }
}
