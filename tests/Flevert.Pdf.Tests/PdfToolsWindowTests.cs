using System.Diagnostics;
using System.Windows.Automation;

namespace Flevert.Pdf.Tests;

/// <summary>
/// The real "Flevert — PDF tools" window of the built flevert.exe, read through UI Automation as a screen reader reads it
/// (docs/specs/pdf.md, v0.2.2, "Trợ năng"). It opens a window on the desktop, so it runs only when asked:
/// set FLEVERT_UI_TESTS=1. Nothing is typed into the session: controls are driven through their automation patterns.
/// </summary>
public sealed class PdfToolsWindowTests : IDisposable
{
    private const string Title = "Flevert — PDF tools";
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [SkippableFact]
    public void Every_control_has_an_accessible_name_and_cancel_closes_with_nothing_changed()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("FLEVERT_UI_TESTS") == "1", "Opens a window on the desktop: set FLEVERT_UI_TESTS=1.");
        var input = _dir.Combine("Hồ sơ.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rotations.pdf"), input);

        using var flevert = Start(input);
        try
        {
            var window = WaitForWindow(flevert.Id);

            var named = window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Where(e => e.Current.ControlType is var type && (type == ControlType.RadioButton || type == ControlType.Button
                    || type == ControlType.Edit || type == ControlType.ComboBox || type == ControlType.Group))
                .ToList();
            Assert.All(named, e => Assert.False(string.IsNullOrWhiteSpace(e.Current.Name), $"a {e.Current.ControlType.ProgrammaticName} has no name"));
            var tools = named.Where(e => e.Current.ControlType == ControlType.RadioButton).Select(e => e.Current.Name).ToList();
            Assert.Equal(PdfTools.All.Select(t => t.Name).Order(), tools.Order());
            // The sentence that says what each tool does is read with it.
            Assert.All(named.Where(e => e.Current.ControlType == ControlType.RadioButton), e => Assert.False(string.IsNullOrWhiteSpace(e.Current.HelpText)));
            Assert.NotNull(Named(window, "1 PDF selected"));
            Assert.NotNull(Named(window, "PDF tools"));
            Assert.NotNull(Named(window, "Actions"));
            // Usable from the keyboard alone: it opens with a tool focused, so Enter runs it and the arrows choose another.
            Assert.Contains(AutomationElement.FocusedElement.Current.Name, tools);

            Invoke(Named(window, "Cancel")!);

            Assert.True(flevert.WaitForExit(15_000), "flevert.exe still running after Cancel");
            Assert.Equal(0, flevert.ExitCode);
            Assert.Equal([input], Directory.GetFiles(_dir.Path));
        }
        finally
        {
            if (!flevert.HasExited)
                flevert.Kill(entireProcessTree: true);
        }
    }

    private Process Start(string input)
    {
        var start = new ProcessStartInfo(TestPdf.FlevertExe) { UseShellExecute = false };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add(PdfToolsOperation.OperationId);
        start.ArgumentList.Add(input);
        start.Environment["FLEVERT_LOG_DIR"] = _logs.Path;
        start.Environment.Remove("FLEVERT_HEADLESS");
        return Process.Start(start)!;
    }

    private static AutomationElement WaitForWindow(int processId)
    {
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, Title),
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (AutomationElement.RootElement.FindFirst(TreeScope.Children, condition) is { } window)
                return window;
            Thread.Sleep(200);
        }

        throw new TimeoutException($"No '{Title}' window within 30 s");
    }

    private static AutomationElement? Named(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

    private static void Invoke(AutomationElement button) =>
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
}
