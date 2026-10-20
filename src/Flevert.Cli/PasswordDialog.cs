using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms.Automation;
using Flevert.Core;
using Flevert.Pdf;

namespace Flevert.Cli;

/// <summary>
/// Asks for PDF passwords in <see cref="PasswordForm"/> on the batch's UI thread (docs/specs/pdf.md, "Mật khẩu"). The
/// batch runs on another thread and waits for the answer; <see cref="BatchPasswords"/> makes sure one question is open at
/// a time and that an answer for all files is not asked again.
/// </summary>
internal sealed class DialogPasswordPrompt : IPasswordPrompt
{
    private SynchronizationContext? _ui;
    private Func<IWin32Window?>? _owner;
    private Action<bool>? _asking;
    private BatchProgress? _latest;

    /// <summary>Called by <see cref="BatchUi"/> on its UI thread once its window exists.</summary>
    /// <param name="owner">The progress window when it is on screen, so the dialog stays above it.</param>
    /// <param name="asking">Told when a dialog opens and closes: the progress window waits for the answer.</param>
    public void Attach(SynchronizationContext ui, Func<IWin32Window?> owner, Action<bool> asking)
    {
        _ui = ui;
        _owner = owner;
        _asking = asking;
    }

    /// <summary>The batch's progress, on the UI thread: how many files an answer could be for.</summary>
    public void Report(BatchProgress progress) => _latest = progress;

    public Task<PasswordAnswer> AskAsync(PasswordQuestion question, CancellationToken ct)
    {
        var ui = _ui ?? throw new InvalidOperationException(PasswordPrompts.NoWindowMessage);
        var answer = new TaskCompletionSource<PasswordAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                _asking?.Invoke(true);
                using var form = new PasswordForm(question.InputPath, question.Purpose, () => FilesLeft(question));
                // The batch was cancelled meanwhile: the answer no longer matters.
                using var cancel = ct.Register(() => ui.Post(_ => form.Close(), null));
                form.ShowDialog(_owner?.Invoke());
                if (ct.IsCancellationRequested)
                {
                    form.Password?.Dispose();
                    answer.TrySetCanceled(ct);
                }
                else
                {
                    answer.TrySetResult(new PasswordAnswer(form.Password, form.Files));
                }
            }
#pragma warning disable CA1031 // Whatever fails here fails that file, through the task; the UI thread carries on.
            catch (Exception e)
#pragma warning restore CA1031
            {
                answer.TrySetException(e);
            }
            finally
            {
                _asking?.Invoke(false);
            }
        }, null);
        return answer.Task;
    }

    // Files of the batch not finished and not already holding a password: what "Use this password for all N files"
    // would cover. Grows while the dialog is open if more files of the selection arrive.
    private int FilesLeft(PasswordQuestion question) =>
        _latest is { } progress ? progress.Total - progress.Completed - question.AnsweredInProgress : 1;
}

/// <summary>
/// The password dialog: a masked field, a second one to protect, and "Use this password for all N files" when the batch
/// has more than one. The password is read from the fields into a <c>char[]</c> that <see cref="PdfPassword"/> takes
/// over, never through <see cref="Control.Text"/> (a string Flevert could not wipe), and the fields are emptied.
/// </summary>
internal sealed partial class PasswordForm : Form
{
    // In 96-DPI pixels: the font is already scaled for the screen, sizes given here are not (seen at 150%: the title cut
    // off, the text wrapped narrow), so they go through LogicalToDeviceUnits.
    private const int FieldWidth = 380;
    private static readonly TimeSpan CountRefresh = TimeSpan.FromMilliseconds(250);

    private readonly PasswordPurpose _purpose;
    private readonly Func<int> _filesLeft;
    // Not a live region either: the dialog's description already says what it is for.
    private readonly Label _heading = new() { AutoSize = true, UseMnemonic = false };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, AccessibleName = "Password" };
    private readonly TextBox _again = new() { UseSystemPasswordChar = true, AccessibleName = "Password again" };
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

    /// <param name="filesLeft">How many files of the batch an answer could be for, read again while the dialog is open.</param>
    public PasswordForm(string inputPath, PasswordPurpose purpose, Func<int> filesLeft)
    {
        _purpose = purpose;
        _filesLeft = filesLeft;
        var name = Path.GetFileName(inputPath);
        var protect = purpose == PasswordPurpose.Protect;
        Text = protect ? "Flevert — Protect with password" : "Flevert — Remove password";
        _heading.Text = protect
            ? $"Choose a password for a protected copy of {name}. It will open only with this password, and Flevert cannot recover it if it is forgotten."
            : $"Type the password of {name}. Flevert writes a copy that opens without one.";
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
        _password.Width = _again.Width = width;
        _heading.Margin = Pad(0, 0, 0, 12);
        _password.Margin = _again.Margin = Pad(0, 0, 0, 0);
        _forAll.Margin = _problem.Margin = Pad(0, 8, 0, 0);

        // No access key on the buttons: on a focused button a bare letter would press it. Enter is OK, Esc is Cancel.
        var ok = new Button { Text = protect ? "Protect" : "Remove password", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(_heading);
        // The labels come right before their fields: a screen reader and the access keys pair them by that order.
        layout.Controls.Add(new Label { Text = "&Password:", AutoSize = true, Margin = Pad(0, 0, 0, 3) });
        layout.Controls.Add(_password);
        if (protect)
        {
            layout.Controls.Add(new Label { Text = "Type it &again:", AutoSize = true, Margin = Pad(0, 8, 0, 3) });
            layout.Controls.Add(_again);
        }

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

    /// <summary>The password typed, or <c>null</c> after Cancel. The caller owns it.</summary>
    public PdfPassword? Password { get; private set; }

    /// <summary>How many files the answer is for: N when "Use this password for all N files" was kept, else 1. With
    /// Cancel too, so Cancel with the box ticked skips them all.</summary>
    /// <remarks>Read after the dialog has closed, when no control of it is <see cref="Control.Visible"/> any more: whether
    /// the box was offered is kept apart (seen in the first run with three files, which then asked twice more).</remarks>
    public int Files => _shownFiles > 1 && _forAll.Checked ? _shownFiles : 1;

    private void ShowFileCount()
    {
        var files = Math.Max(_filesLeft(), 1);
        if (files == _shownFiles)
            return;
        _shownFiles = files;
        _forAll.Text = $"&Use this password for all {files} files";
        _forAll.Visible = files > 1;
    }

    private void Accept()
    {
        var password = ReadField(_password);
        var again = _purpose == PasswordPurpose.Protect ? ReadField(_again) : [];
        try
        {
            if (PasswordRules.Problem(password, again, _purpose) is { } problem)
            {
                ShowProblem(problem, problem == PasswordRules.MismatchMessage ? _again : _password);
                return;
            }

            // The count the user saw when choosing OK, not one that grows after.
            _count.Stop();
            Password = new PdfPassword(password);
            password = null; // taken over
            DialogResult = DialogResult.OK;
        }
        finally
        {
            if (password is not null)
                Wipe(password);
            Wipe(again);
        }
    }

    private void ShowProblem(string problem, TextBox field)
    {
        _problem.Text = problem;
        _problem.AccessibilityObject.RaiseLiveRegionChanged();
        // Also said when the field gets the focus.
        _password.AccessibleDescription = _again.AccessibleDescription = null;
        field.AccessibleDescription = problem;
        field.Focus();
        field.SelectAll();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The count the user saw when choosing, also for Cancel.
        _count.Stop();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Whatever was typed leaves the fields; the edit control's own memory is beyond reach.
        _password.Clear();
        _again.Clear();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _count.Dispose();
        base.Dispose(disposing);
    }

    private static void Wipe(char[] chars) => CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));

    // The field's characters straight from the edit control into an array Flevert can wipe. Control.Text would make a
    // string, which stays in memory until the garbage collector reuses it.
    private static unsafe char[] ReadField(TextBox field)
    {
        var length = GetWindowTextLength(field.Handle);
        var buffer = new char[length + 1]; // room for the terminating null
        try
        {
            int read;
            fixed (char* chars = buffer)
                read = GetWindowText(field.Handle, chars, buffer.Length);
            return buffer.AsSpan(0, read).ToArray();
        }
        finally
        {
            Wipe(buffer);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint window);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(nint window, char* text, int maxCount);
}
