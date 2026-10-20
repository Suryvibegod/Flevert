using System.Globalization;
using Flevert.Core;

namespace Flevert.Pdf;

/// <summary>Tools of one group of the PDF tools window, under its heading.</summary>
public sealed record PdfToolSection(PdfToolGroup Group, string Title, IReadOnlyList<PdfTool> Tools);

/// <summary>
/// What the PDF tools window shows and checks, without the window (docs/specs/pdf.md, v0.2.2, "Cửa sổ PDF tools"): the
/// tools by group, the tool selected (the one chosen last time, else the first), the values of its options starting from
/// their defaults, and the reason Run cannot go ahead yet.
/// </summary>
public sealed class PdfToolPicker
{
    /// <summary>Shown in an empty text option, such as the watermark's.</summary>
    public const string TextHint = "For example: Chỉ dùng để nộp hồ sơ …";

    private static readonly (PdfToolGroup Group, string Title)[] Order =
        [(PdfToolGroup.Pages, "Pages"), (PdfToolGroup.AddToPages, "Add to pages"), (PdfToolGroup.Extract, "Extract")];

    // Per tool, kept while the window is open: going to another tool and back does not lose what was typed.
    private readonly Dictionary<string, Dictionary<string, string>> _values = new(StringComparer.Ordinal);

    /// <param name="tools">In the order the window lists them within each group: the most used first.</param>
    /// <param name="lastToolId">The tool chosen last time; ignored when it is not one of <paramref name="tools"/>.</param>
    public PdfToolPicker(IReadOnlyList<PdfTool> tools, string? lastToolId)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count == 0)
            throw new ArgumentException("There is no PDF tool to pick.", nameof(tools));
        Sections = [.. Order
            .Select(g => new PdfToolSection(g.Group, g.Title, [.. tools.Where(t => t.Group == g.Group)]))
            .Where(s => s.Tools.Count > 0)];
        // A group added later still shows, after the known ones.
        var unknown = tools.Where(t => !Order.Any(g => g.Group == t.Group)).ToList();
        if (unknown.Count > 0)
            Sections = [.. Sections, new PdfToolSection(unknown[0].Group, "More", unknown)];
        Tools = [.. Sections.SelectMany(s => s.Tools)];
        Selected = Tools.FirstOrDefault(t => t.OperationId == lastToolId) ?? Tools[0];
    }

    public IReadOnlyList<PdfToolSection> Sections { get; }

    /// <summary>Every tool, in the order shown.</summary>
    public IReadOnlyList<PdfTool> Tools { get; }

    public PdfTool Selected { get; private set; }

    /// <summary>"3 PDFs selected": the line at the top of the window.</summary>
    public static string SelectedLine(int files) =>
        files == 1 ? "1 PDF selected" : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(files, 0)} PDFs selected");

    public void Select(string operationId) =>
        Selected = Tools.FirstOrDefault(t => t.OperationId == operationId)
            ?? throw new ArgumentException($"No PDF tool '{operationId}'.", nameof(operationId));

    /// <summary>The value of an option of the selected tool: what was set, else its default.</summary>
    public string Value(PdfToolOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return _values.TryGetValue(Selected.OperationId, out var values) && values.TryGetValue(option.Key, out var value)
            ? value
            : option.Default;
    }

    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Selected.Options.Any(o => o.Key == key))
            throw new ArgumentException($"{Selected.Name} has no option '{key}'.", nameof(key));
        if (!_values.TryGetValue(Selected.OperationId, out var values))
            _values[Selected.OperationId] = values = new Dictionary<string, string>(StringComparer.Ordinal);
        values[key] = value;
    }

    /// <summary>Why Run cannot go ahead, said to the user, with the option it is about; <c>null</c> when it can.</summary>
    public (PdfToolOption Option, string Message)? Problem()
    {
        foreach (var option in Selected.Options)
        {
            var value = Value(option);
            switch (option.Kind)
            {
                case PdfToolOptionKind.Text:
                    // Every text option is something to write on the pages: there is nothing to write without it.
                    if (string.IsNullOrWhiteSpace(value))
                        return (option, $"Type the {Lower(option)}.");
                    // Counted as the text box counts, in UTF-16 units (a Vietnamese letter typed with Telex or VNI is one).
                    if (option.MaxLength > 0 && value.Length > option.MaxLength)
                        return (option, string.Create(CultureInfo.InvariantCulture, $"The {Lower(option)} can be at most {option.MaxLength} characters."));
                    if (value.Any(char.IsControl))
                        return (option, $"The {Lower(option)} must be one line of text.");
                    break;
                case PdfToolOptionKind.Choice:
                    if (!option.Choices.Contains(value, StringComparer.Ordinal))
                        return (option, $"Choose a {Lower(option)}.");
                    break;
            }
        }

        return null;
    }

    private static string Lower(PdfToolOption option) => option.Label.TrimEnd(':', ' ').ToLowerInvariant();

    /// <summary>The answer for the whole batch: the selected tool with the value of each option (text trimmed).</summary>
    /// <exception cref="InvalidOperationException">There is a <see cref="Problem"/>.</exception>
    public PdfToolChoice Answer()
    {
        if (Problem() is { } problem)
            throw new InvalidOperationException(problem.Message);
        return new PdfToolChoice(Selected, Selected.Options.ToDictionary(
            o => o.Key, o => o.Kind == PdfToolOptionKind.Text ? Value(o).Trim() : Value(o), StringComparer.Ordinal));
    }
}

/// <summary>
/// Remembers the tool chosen last in the PDF tools window: only its id, in %LOCALAPPDATA%\Flevert\settings (no file
/// names, no text typed). Reading never fails: a missing or damaged setting means none.
/// </summary>
public sealed class PdfToolSettings(string path)
{
    private const int MaxIdLength = 64;

    public static string DefaultPath { get; } = Path.Combine(AppData.Root, AppData.SettingsFolder, "last-pdf-tool.txt");

    public string? LastToolId()
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxIdLength)
                return null;
            var id = File.ReadAllText(path).Trim();
            return id.Length > 0 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-') ? id : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Best effort: the tool still runs if the setting cannot be written.</summary>
    public void Remember(string toolId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, toolId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
