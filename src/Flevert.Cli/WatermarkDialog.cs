using System.Runtime.InteropServices;
using System.Windows.Forms.Automation;
using Flevert.Core;
using Flevert.Pdf;

namespace Flevert.Cli;

/// <summary>
/// Asks for the text of <c>Add watermark…</c> in <see cref="WatermarkForm"/> on the batch's UI thread, as
/// <see cref="DialogPasswordPrompt"/> asks for passwords: the batch waits for the answer, and <see cref="BatchWatermarks"/>
/// makes sure one question is open at a time and that an answer for all files is not asked again.
/// </summary>
internal sealed class DialogWatermarkPrompt : IWatermarkPrompt, IBatchQuestion
{
    private BatchQuestionHost? _host;
    private BatchProgress? _latest;
    private int _asked;

    public void Attach(BatchQuestionHost host) => _host = host;

    public void Report(BatchProgress progress) => _latest = progress;

    public Task<WatermarkAnswer> AskAsync(WatermarkQuestion question, CancellationToken ct)
    {
        var host = _host ?? throw new InvalidOperationException(WatermarkPrompts.NoWindowMessage);
        var answer = new TaskCompletionSource<WatermarkAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Ui.Post(_ =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                host.Asking(true);
                // The first question comes from the user's own click in the menu; a later one comes while they may be
                // typing elsewhere, so it waits in the taskbar instead of taking their keys.
                using var form = new WatermarkForm(question.InputPath, () => FilesLeft(question), activate: _asked++ == 0);
                // The batch was cancelled meanwhile: the answer no longer matters.
                using var cancel = ct.Register(() => host.Ui.Post(_ => form.Close(), null));
                form.ShowDialog(host.Owner());
                if (ct.IsCancellationRequested)
                    answer.TrySetCanceled(ct);
                else
                    answer.TrySetResult(new WatermarkAnswer(form.Watermark, form.Files));
            }
#pragma warning disable CA1031 // Whatever fails here fails that file, through the task; the UI thread carries on.
            catch (Exception e)
#pragma warning restore CA1031
            {
                answer.TrySetException(e);
            }
            finally
            {
                host.Asking(false);
            }
        }, null);
        return answer.Task;
    }

    // Files of the batch not finished and not already holding a text: what "Use this text for all N files" would cover.
    private int FilesLeft(WatermarkQuestion question) =>
        _latest is { } progress ? progress.Total - progress.Completed - question.AnsweredInProgress : 1;
}

/// <summary>
/// "Flevert — Add watermark": the text to write across every page, and "Use this text for all N files" when the batch has
/// more than one. Built like the password dialog: on top, named for screen readers, Enter adds the watermark, Esc cancels.
/// </summary>
internal sealed partial class WatermarkForm : Form
{
    // In 96-DPI pixels, scaled with LogicalToDeviceUnits (the font is already scaled for the screen, sizes are not).
    private const int FieldWidth = 380;
    private static readonly TimeSpan CountRefresh = TimeSpan.FromMilliseconds(250);

    private readonly Func<int> _filesLeft;
    private readonly Label _heading = new() { AutoSize = true, UseMnemonic = false };
    private readonly TextBox _text = new()
    {
        AccessibleName = "Watermark text",
        PlaceholderText = "For example: Chỉ dùng để nộp hồ sơ …",
        MaxLength = WatermarkPrompts.FieldMaxLength,
    };
    private readonly CheckBox _forAll = new() { AutoSize = true, Checked = true, Visible = false };
    // Read out as soon as it changes: the reason OK did nothing.
    private readonly Label _problem = new()
    {
        AutoSize = true,
        ForeColor = Color.Firebrick,
        UseMnemonic = false,
        LiveSetting = AutomationLiveSetting.Assertive,
    };
    private readonly System.Windows.Forms.Timer _count = new() { Interval = (int)CountRefresh.TotalMilliseconds };
    private int _shownFiles;
    private readonly bool _activate;
    private readonly EnterGuard _enter = new();

    /// <param name="filesLeft">How many files of the batch an answer could be for, read again while the dialog is open.</param>
    /// <param name="activate">Whether the dialog takes the focus when it opens; when not, it flashes in the taskbar.</param>
    public WatermarkForm(string inputPath, Func<int> filesLeft, bool activate = true)
    {
        _filesLeft = filesLeft;
        _activate = activate;
        Text = "Flevert — Add watermark";
        _heading.Text = $"Type the text to write across every page of {Path.GetFileName(inputPath)}, such as what the copy is for. " +
            "It marks the copy; anyone with a PDF editor can still remove it.";
        AccessibleDescription = _heading.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // Like the progress window: by the time it opens, the window the user clicked in may be in front.
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding Pad(int left, int top, int right, int bottom) =>
            new(LogicalToDeviceUnits(left), LogicalToDeviceUnits(top), LogicalToDeviceUnits(right), LogicalToDeviceUnits(bottom));
        Padding = Pad(12, 12, 12, 12);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var width = LogicalToDeviceUnits(FieldWidth);
        _heading.MaximumSize = _problem.MaximumSize = new Size(width, 0);
        _text.Width = width;
        _heading.Margin = Pad(0, 0, 0, 12);
        _text.Margin = Pad(0, 0, 0, 0);
        _forAll.Margin = _problem.Margin = Pad(0, 8, 0, 0);
        _text.TextChanged += (_, _) => ClearProblem();
        _text.GotFocus += (_, _) => _enter.FieldFocused();
        _text.LostFocus += (_, _) => _enter.FieldLeft();
        _text.MouseDown += (_, _) => _enter.FieldClicked();

        // No access key on the buttons: on a focused button a bare letter would press it. Enter is OK, Esc is Cancel.
        var ok = new Button { Text = "Add watermark", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(_heading);
        // The label comes right before its field: a screen reader and the access key pair them by that order.
        layout.Controls.Add(new Label { Text = "&Text:", AutoSize = true, Margin = Pad(0, 0, 0, 3) });
        layout.Controls.Add(_text);
        layout.Controls.Add(_forAll);
        layout.Controls.Add(_problem);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = Pad(0, 12, 0, 0),
            AccessibleRole = AccessibleRole.Grouping,
            AccessibleName = "Actions",
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        ShowFileCount();
        _count.Tick += (_, _) => ShowFileCount();
        _count.Start();
    }

    /// <summary>The text typed, trimmed, or <c>null</c> after Cancel.</summary>
    /// <remarks>Not <see cref="Control.Text"/>, which is the window's title.</remarks>
    public string? Watermark { get; private set; }

    /// <summary>How many files the answer is for: N when "Use this text for all N files" was kept, else 1. With Cancel
    /// too, so Cancel with the box ticked skips them all.</summary>
    /// <remarks>Read after the dialog has closed, from the count shown, not from <see cref="Control.Visible"/> (false for
    /// every control of a closed form: the password dialog learnt this in its first real run).</remarks>
    public int Files => _shownFiles > 1 && _forAll.Checked ? _shownFiles : 1;

    private void ShowFileCount()
    {
        var files = Math.Max(_filesLeft(), 1);
        if (files == _shownFiles)
            return;
        _shownFiles = files;
        _forAll.Text = $"&Use this text for all {files} files";
        _forAll.Visible = files > 1;
    }

    private void Accept()
    {
        if (WatermarkPrompts.Problem(_text.Text) is { } problem)
        {
            _problem.Text = problem;
            _problem.AccessibilityObject.RaiseLiveRegionChanged();
            // Also said when the field gets the focus.
            _text.AccessibleDescription = problem;
            _text.Focus();
            _text.SelectAll();
            return;
        }

        // The count the user saw when choosing OK, not one that grows after.
        _count.Stop();
        Watermark = _text.Text.Trim();
        DialogResult = DialogResult.OK;
    }

    protected override bool ShowWithoutActivation => !_activate;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_activate)
        {
            var flash = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = Handle,
                Flags = FlashTray | FlashUntilForeground,
            };
            FlashWindowEx(ref flash);
        }
    }

    // Enter that comes before the field has settled is dropped: it was typed for another window (EnterGuard). The mouse on
    // Add watermark still works.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && !_enter.EnterCounts)
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ClearProblem()
    {
        if (_problem.Text.Length == 0)
            return;
        _problem.Text = "";
        _text.AccessibleDescription = null;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The count the user saw when choosing, also for Cancel.
        _count.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _count.Dispose();
        base.Dispose(disposing);
    }

    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);
}
