using System.Windows.Forms.Automation;
using Flevert.Core;
using Flevert.Core.Menu;
using Flevert.Pdf;

namespace Flevert.Cli;

/// <summary>
/// Asks which PDF tool to run in <see cref="PdfToolsForm"/> on the batch's UI thread, once for the whole batch
/// (<see cref="PdfToolsOperation"/> asks once). Run remembers the tool for next time; Cancel ends the batch quietly.
/// </summary>
internal sealed class DialogPdfToolPrompt(IReadOnlyList<PdfTool> tools, PdfToolSettings settings) : IPdfToolPrompt, IBatchQuestion
{
    private BatchQuestionHost? _host;
    private BatchProgress? _latest;

    public void Attach(BatchQuestionHost host) => _host = host;

    public void Report(BatchProgress progress) => _latest = progress;

    public Task<PdfToolChoice?> AskAsync(CancellationToken ct)
    {
        var host = _host ?? throw new InvalidOperationException(PdfToolPrompts.NoWindowMessage);
        var answer = new TaskCompletionSource<PdfToolChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Ui.Post(_ =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                host.Asking(true);
                var picker = new PdfToolPicker(tools, settings.LastToolId());
                using var form = new PdfToolsForm(picker, () => _latest?.Total ?? 1, MenuIcons.Load);
                using var cancel = ct.Register(() => host.Ui.Post(_ => form.Close(), null));
                form.ShowDialog(host.Owner());
                if (ct.IsCancellationRequested)
                {
                    answer.TrySetCanceled(ct);
                }
                else if (form.Choice is { } choice)
                {
                    settings.Remember(choice.Tool.OperationId);
                    host.Retitle(choice.Tool.Name);
                    answer.TrySetResult(choice);
                }
                else
                {
                    // Cancel: nothing changes, and no notification says so.
                    host.EndQuietly();
                    answer.TrySetResult(null);
                }
            }
#pragma warning disable CA1031 // Whatever fails here fails the batch's files, through the task; the UI thread carries on.
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
}

/// <summary>The menu's icons, from the icons folder beside flevert.exe (<see cref="MenuInstaller.IconsFolder"/>).</summary>
internal static class MenuIcons
{
    private const string Fallback = "document";

    /// <summary>The Fluent icon <paramref name="name"/> drawn at <paramref name="size"/> pixels; the generic document icon
    /// when there is no such icon; <c>null</c> when there is none at all (a development build without icons).</summary>
    public static Bitmap? Load(string name, int size)
    {
        foreach (var candidate in new[] { PdfToolPicker.IconFile(name), Fallback })
        {
            // Names come from Flevert's own tool list; anything else is not a file name.
            if (candidate.Length == 0 || !candidate.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
                continue;
            var path = Path.Combine(AppContext.BaseDirectory, MenuInstaller.IconsFolder, candidate + ".ico");
            if (!File.Exists(path))
                continue;
            try
            {
                using var icon = new Icon(path, size, size);
                using var frame = icon.ToBitmap();
                // The nearest size the file holds, drawn at the size asked for.
                var bitmap = new Bitmap(size, size);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(frame, 0, 0, size, size);
                return bitmap;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}

/// <summary>
/// "Flevert — PDF tools" (docs/specs/pdf.md, v0.2.2): how many PDFs are selected, the tools by group, each with its icon,
/// name and what it does, the options of the selected tool under them, Run (Enter) and Cancel (Esc). Like the password
/// dialog it stays on top and every control has an accessible name; the tools are radio buttons, so the arrow keys move
/// between them and Tab goes on to the options and the buttons.
/// </summary>
internal sealed class PdfToolsForm : Form
{
    // In 96-DPI pixels, scaled with LogicalToDeviceUnits (the font is already scaled for the screen, sizes are not).
    private const int ListWidth = 470;
    private const int ListHeight = 340;
    private const int IconSize = 24;
    private const int DescriptionIndent = 54;
    private static readonly TimeSpan CountRefresh = TimeSpan.FromMilliseconds(250);

    private readonly PdfToolPicker _picker;
    private readonly Func<int> _files;
    private readonly Label _count = new() { AutoSize = true, UseMnemonic = false };
    private readonly Panel _list = new() { AutoScroll = true, AccessibleName = "PDF tools", AccessibleRole = AccessibleRole.Grouping };
    private readonly GroupBox _options = new() { Text = "Options", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly TableLayoutPanel _optionRows = new() { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
    // Read out as soon as it changes: the reason Run did nothing.
    private readonly Label _problem = new()
    {
        AutoSize = true,
        ForeColor = Color.Firebrick,
        UseMnemonic = false,
        LiveSetting = AutomationLiveSetting.Assertive,
    };
    private readonly Dictionary<string, RadioButton> _radios = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Control> _optionControls = new(StringComparer.Ordinal);
    private readonly List<System.Drawing.Image> _images = [];
    private readonly Font _countFont;
    private readonly Font _headingFont;
    private readonly System.Windows.Forms.Timer _countTimer = new() { Interval = (int)CountRefresh.TotalMilliseconds };
    private int _shownFiles = -1;

    /// <param name="files">How many PDFs the batch has, read again while the window is open (more may arrive).</param>
    /// <param name="icon">Draws a Fluent icon at a size in pixels, or <c>null</c> when there is none.</param>
    public PdfToolsForm(PdfToolPicker picker, Func<int> files, Func<string, int, Bitmap?> icon)
    {
        _picker = picker;
        _files = files;
        Text = "Flevert — PDF tools";
        AccessibleDescription = "Choose what to do with the selected PDFs.";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // Like the progress window: by the time it opens, the window the user clicked in may be in front.
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Pad(12, 12, 12, 12);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);

        _countFont = new Font(Font.FontFamily, Font.SizeInPoints * 1.15f, FontStyle.Bold);
        _headingFont = new Font(Font, FontStyle.Bold);
        _count.Font = _countFont;
        _count.Margin = Pad(0, 0, 0, 8);
        _list.Size = new Size(LogicalToDeviceUnits(ListWidth), LogicalToDeviceUnits(ListHeight));
        _list.Margin = Pad(0, 0, 0, 8);
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Controls.Add(BuildList(icon));
        _options.Width = _list.Width;
        _options.MinimumSize = new Size(_list.Width, LogicalToDeviceUnits(64));
        _options.Padding = Pad(8, 4, 8, 4);
        _options.Controls.Add(_optionRows);
        _problem.MaximumSize = new Size(_list.Width, 0);
        _problem.Margin = Pad(0, 6, 0, 0);

        // No access key on the buttons: on a focused button a bare letter would press it. Enter is Run, Esc is Cancel.
        var run = new Button { Text = "Run", AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(88), 0), TabIndex = 0 };
        var cancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            MinimumSize = new Size(LogicalToDeviceUnits(88), 0),
            DialogResult = DialogResult.Cancel,
            TabIndex = 1,
        };
        run.Click += (_, _) => Accept();
        AcceptButton = run;
        CancelButton = cancel;
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
        buttons.Controls.Add(run);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(_count);
        layout.Controls.Add(_list);
        layout.Controls.Add(_options);
        layout.Controls.Add(_problem);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        ShowSelected();
        ShowFileCount();
        _countTimer.Tick += (_, _) => ShowFileCount();
        _countTimer.Start();
    }

    /// <summary>The tool and options chosen, or <c>null</c> after Cancel.</summary>
    public PdfToolChoice? Choice { get; private set; }

    private Padding Pad(int left, int top, int right, int bottom) =>
        new(LogicalToDeviceUnits(left), LogicalToDeviceUnits(top), LogicalToDeviceUnits(right), LogicalToDeviceUnits(bottom));

    private TableLayoutPanel BuildList(Func<string, int, Bitmap?> icon)
    {
        var rows = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Location = Point.Empty, Padding = Pad(8, 4, 8, 8) };
        var width = LogicalToDeviceUnits(ListWidth - 40);
        var tabIndex = 0;
        foreach (var section in _picker.Sections)
        {
            rows.Controls.Add(new Label
            {
                Text = section.Title,
                AutoSize = true,
                UseMnemonic = false,
                Font = _headingFont,
                Margin = Pad(0, rows.Controls.Count == 0 ? 4 : 12, 0, 4),
                AccessibleRole = AccessibleRole.StaticText,
            });
            foreach (var tool in section.Tools)
            {
                var image = icon(tool.Icon, LogicalToDeviceUnits(IconSize));
                if (image is not null)
                    _images.Add(image);
                var radio = new RadioButton
                {
                    Text = tool.Name,
                    AutoSize = true,
                    UseMnemonic = false,
                    Image = image,
                    ImageAlign = ContentAlignment.MiddleLeft,
                    TextImageRelation = TextImageRelation.ImageBeforeText,
                    Margin = Pad(0, 2, 0, 0),
                    // The group heading and the sentence under the name, read with it.
                    AccessibleName = tool.Name,
                    AccessibleDescription = $"{section.Title}: {tool.Description}",
                    TabIndex = tabIndex++,
                    Checked = tool == _picker.Selected,
                };
                radio.CheckedChanged += (_, _) =>
                {
                    if (!radio.Checked)
                        return;
                    _picker.Select(tool.OperationId);
                    ShowSelected();
                };
                _radios[tool.OperationId] = radio;
                rows.Controls.Add(radio);
                rows.Controls.Add(new Label
                {
                    Text = tool.Description,
                    AutoSize = true,
                    UseMnemonic = false,
                    ForeColor = SystemColors.GrayText,
                    MaximumSize = new Size(width - LogicalToDeviceUnits(DescriptionIndent), 0),
                    Margin = Pad(DescriptionIndent, 0, 0, 4),
                });
            }
        }

        return rows;
    }

    // The options of the selected tool under the list; the window keeps its size when a tool has none.
    private void ShowSelected()
    {
        var tool = _picker.Selected;
        _optionRows.SuspendLayout();
        foreach (Control control in _optionRows.Controls.Cast<Control>().ToList())
            control.Dispose();
        _optionRows.Controls.Clear();
        _optionControls.Clear();
        _options.Text = $"Options: {tool.Name}";
        _options.AccessibleName = $"Options for {tool.Name}";
        ClearProblem();

        if (tool.Options.Count == 0)
        {
            var none = new Label { Text = "Nothing to set: choose Run.", AutoSize = true, UseMnemonic = false, ForeColor = SystemColors.GrayText, Margin = Pad(0, 6, 0, 6) };
            _optionRows.Controls.Add(none);
            _optionRows.SetColumnSpan(none, 2);
        }

        var fieldWidth = LogicalToDeviceUnits(ListWidth - 170);
        foreach (var option in tool.Options)
        {
            // The label comes right before its field: a screen reader pairs them by that order.
            _optionRows.Controls.Add(new Label
            {
                Text = option.Label.TrimEnd(':', ' ') + ":",
                AutoSize = true,
                UseMnemonic = false,
                Anchor = AnchorStyles.Left,
                Margin = Pad(0, 6, 8, 6),
            });
            Control field;
            if (option.Kind == PdfToolOptionKind.Choice)
            {
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = fieldWidth, AccessibleName = option.Label.TrimEnd(':', ' ') };
                // Shown as words, kept as the values the tool takes, by position.
                combo.Items.AddRange([.. option.Choices.Select(PdfToolPicker.ChoiceText)]);
                combo.SelectedIndex = option.Choices.ToList().IndexOf(_picker.Value(option));
                combo.SelectedIndexChanged += (_, _) =>
                {
                    if (combo.SelectedIndex >= 0)
                        _picker.Set(option.Key, option.Choices[combo.SelectedIndex]);
                };
                field = combo;
            }
            else
            {
                var text = new TextBox
                {
                    Width = fieldWidth,
                    MaxLength = option.MaxLength > 0 ? option.MaxLength : 32767,
                    PlaceholderText = PdfToolPicker.TextHint,
                    AccessibleName = option.Label.TrimEnd(':', ' '),
                    Text = _picker.Value(option),
                };
                text.TextChanged += (_, _) =>
                {
                    _picker.Set(option.Key, text.Text);
                    ClearProblem();
                };
                field = text;
            }

            field.Margin = Pad(0, 3, 0, 3);
            _optionControls[option.Key] = field;
            _optionRows.Controls.Add(field);
        }

        _optionRows.ResumeLayout();
        if (_radios.TryGetValue(tool.OperationId, out var radio))
            _list.ScrollControlIntoView(radio);
    }

    private void ShowFileCount()
    {
        var files = Math.Max(_files(), 1);
        if (files == _shownFiles)
            return;
        _shownFiles = files;
        _count.Text = PdfToolPicker.SelectedLine(files);
    }

    private void Accept()
    {
        if (_picker.Problem() is { } problem)
        {
            _problem.Text = problem.Message;
            _problem.AccessibilityObject.RaiseLiveRegionChanged();
            if (_optionControls.TryGetValue(problem.Option.Key, out var field))
            {
                field.AccessibleDescription = problem.Message;
                field.Focus();
            }

            return;
        }

        _countTimer.Stop();
        Choice = _picker.Answer();
        DialogResult = DialogResult.OK;
    }

    private void ClearProblem()
    {
        _problem.Text = "";
        foreach (var field in _optionControls.Values)
            field.AccessibleDescription = null;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The tool chosen last time is selected: Enter runs it again.
        if (_radios.TryGetValue(_picker.Selected.OperationId, out var radio))
            radio.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _countTimer.Stop();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _countTimer.Dispose();
            _countFont.Dispose();
            _headingFont.Dispose();
            foreach (var image in _images)
                image.Dispose();
        }

        base.Dispose(disposing);
    }
}
