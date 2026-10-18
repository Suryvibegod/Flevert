using ConvertAnything.Core;

namespace ConvertAnything.Cli;

internal sealed class ProgressForm : Form
{
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Height = 40, AutoEllipsis = true };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 18 };
    private readonly Button _cancel = new() { Text = "Cancel", Anchor = AnchorStyles.Right | AnchorStyles.Bottom, AutoSize = true };

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

        _bar.Maximum = total;
        _cancel.Click += (_, _) =>
        {
            _cancel.Enabled = false;
            _status.Text = "Cancelling after the current file…";
            cancellation.Cancel();
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        buttons.Controls.Add(_cancel);
        Controls.Add(buttons);
        Controls.Add(_bar);
        Controls.Add(_status);
        CancelButton = _cancel;
    }

    public void Report(BatchProgress progress)
    {
        _bar.Value = Math.Min(progress.Completed, _bar.Maximum);
        if (progress.Current is { } file && _cancel.Enabled)
            _status.Text = $"{progress.Completed + 1} of {progress.Total}: {Path.GetFileName(file)}";
    }
}
