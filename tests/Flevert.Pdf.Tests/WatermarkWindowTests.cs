using System.Diagnostics;
using System.Windows.Automation;

namespace Flevert.Pdf.Tests;

/// <summary>
/// The real "Flevert — Add watermark" dialog of the built flevert.exe, read through UI Automation as a screen reader reads
/// it (docs/specs/pdf.md, v0.2.2). It opens a window on the desktop, so it runs only when asked: set FLEVERT_UI_TESTS=1.
/// Nothing is typed into the session: controls are driven through their automation patterns.
/// </summary>
public sealed class WatermarkWindowTests : IDisposable
{
    private const string Title = "Flevert — Add watermark";
    private readonly TempDir _dir = new();
    private readonly TempDir _logs = new();

    public void Dispose()
    {
        _dir.Dispose();
        _logs.Dispose();
    }

    [SkippableFact]
    public void Every_control_is_named_an_empty_text_is_refused_and_cancel_leaves_the_pdf_as_it_is()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("FLEVERT_UI_TESTS") == "1", "Opens a window on the desktop: set FLEVERT_UI_TESTS=1.");
        var input = _dir.Combine("Hồ sơ.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rotations.pdf"), input);

        using var flevert = Start(input);
        try
        {
            var window = WaitForWindow(flevert.Id);
            var controls = window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Where(e => e.Current.ControlType is var type && (type == ControlType.Button || type == ControlType.Edit
                    || type == ControlType.CheckBox || type == ControlType.Group))
                .ToList();
            Assert.All(controls, e => Assert.False(string.IsNullOrWhiteSpace(e.Current.Name), $"a {e.Current.ControlType.ProgrammaticName} has no name"));
            Assert.Equal(ControlType.Edit, Named(window, "Watermark text")!.Current.ControlType);
            Assert.NotNull(Named(window, "Actions"));
            // One file: no "Use this text for all N files".
            Assert.DoesNotContain(controls, e => e.Current.ControlType == ControlType.CheckBox && e.Current.IsOffscreen == false);

            Invoke(Named(window, "Add watermark")!);
            Assert.True(WaitFor(() => Named(window, WatermarkPrompts.EmptyMessage) is not null), "no reason given for refusing an empty text");
            Assert.False(flevert.HasExited);

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
        start.ArgumentList.Add(PdfWatermarkMenu.OperationId);
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
        AutomationElement? window = null;
        Assert.True(WaitFor(() => (window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition)) is not null), $"No '{Title}' window within 30 s");
        return window!;
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            Thread.Sleep(200);
        }

        return false;
    }

    private static AutomationElement? Named(AutomationElement root, string name) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));

    private static void Invoke(AutomationElement button) =>
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
}
