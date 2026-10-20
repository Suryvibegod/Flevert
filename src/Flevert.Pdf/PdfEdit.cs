using System.Globalization;
using System.Text;

namespace Flevert.Pdf;

/// <summary>What the PDF worker writes for a request to edit (<see cref="PdfWorkerTask.Edit"/>).</summary>
internal enum PdfEditKind
{
    /// <summary>A page number on every page (<see cref="PdfPageNumbers"/>).</summary>
    PageNumbers,

    /// <summary>A text repeated across every page (<see cref="PdfWatermark"/>).</summary>
    Watermark,

    /// <summary>Two pages side by side on landscape A4 sheets (<see cref="PdfNUp"/>).</summary>
    TwoPerSheet,

    /// <summary>Four pages, two by two, on portrait A4 sheets (<see cref="PdfNUp"/>).</summary>
    FourPerSheet,

    /// <summary>Every page scaled onto A4 (<see cref="PdfFitA4"/>).</summary>
    FitA4,
}

/// <summary>Where <see cref="PdfPageNumbers"/> writes the number, on the page as it is shown.</summary>
public enum PdfPageNumberPosition
{
    BottomCenter,
    BottomRight,
    TopRight,
}

/// <summary>How <see cref="PdfPageNumbers"/> writes the number of page 1 of 12.</summary>
public enum PdfPageNumberStyle
{
    /// <summary><c>1</c></summary>
    Number,

    /// <summary><c>1 / 12</c></summary>
    OfTotal,

    /// <summary><c>Page 1 of 12</c></summary>
    Words,
}

/// <summary>
/// An edit the PDF worker makes to every page and saves (<see cref="PdfWorker.EditAsync"/>), with its options. It travels
/// in the request's options field: the kind and the options joined by U+001F, which no option holds (watermark text
/// refuses control characters, <see cref="WatermarkText"/>).
/// </summary>
internal sealed record PdfEdit(PdfEditKind Kind)
{
    private const char Separator = '\u001F';

    /// <summary>The longest watermark, in characters as people count them (text elements).</summary>
    public const int MaxWatermarkLength = 100;

    public PdfPageNumberPosition Position { get; init; }

    public PdfPageNumberStyle Style { get; init; }

    /// <summary>For <see cref="PdfEditKind.Watermark"/>: the text, already checked (<see cref="WatermarkText"/>).</summary>
    public string Text { get; init; } = "";

    public string Encode() => Kind switch
    {
        PdfEditKind.PageNumbers => string.Join(Separator, "numbers", ((int)Position).ToString(CultureInfo.InvariantCulture), ((int)Style).ToString(CultureInfo.InvariantCulture)),
        PdfEditKind.Watermark => string.Join(Separator, "watermark", Text),
        PdfEditKind.TwoPerSheet => "2up",
        PdfEditKind.FourPerSheet => "4up",
        PdfEditKind.FitA4 => "a4",
        _ => throw new InvalidOperationException($"Unknown edit {Kind}."),
    };

    /// <exception cref="FormatException">Not an edit this version writes.</exception>
    public static PdfEdit Decode(string text)
    {
        var parts = text.Split(Separator);
        return (parts[0], parts.Length) switch
        {
            ("numbers", 3) => new PdfEdit(PdfEditKind.PageNumbers)
            {
                Position = Enum<PdfPageNumberPosition>(parts[1]),
                Style = Enum<PdfPageNumberStyle>(parts[2]),
            },
            ("watermark", 2) => new PdfEdit(PdfEditKind.Watermark)
            {
                Text = WatermarkText(parts[1]) ?? throw new FormatException("Not a watermark text."),
            },
            ("2up", 1) => new PdfEdit(PdfEditKind.TwoPerSheet),
            ("4up", 1) => new PdfEdit(PdfEditKind.FourPerSheet),
            ("a4", 1) => new PdfEdit(PdfEditKind.FitA4),
            _ => throw new FormatException("Not an edit of this version."),
        };
    }

    private static T Enum<T>(string text)
        where T : struct, Enum =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && System.Enum.IsDefined(typeof(T), value)
            ? (T)(object)value
            : throw new FormatException($"Not a {typeof(T).Name}: {text}");

    /// <summary>
    /// The watermark as written: <paramref name="text"/> in composed form (NFC: a Vietnamese letter is one character, as
    /// Arial draws it), without the spaces it starts or ends with; <c>null</c> when <see cref="Check"/> finds a problem.
    /// </summary>
    public static string? WatermarkText(string text) => Check(text, out var normal) is null ? normal : null;

    /// <summary>
    /// What is wrong with <paramref name="text"/> as a watermark, or <c>null</c>, with the text as written in
    /// <paramref name="normal"/>: it must not be empty; must be one line (no control character, no U+2028 or U+2029); must
    /// hold no invisible character (a format character such as a direction override or a zero-width space, a lone
    /// surrogate, a noncharacter), which would make the mark read other than it shows; and may hold at most
    /// <see cref="MaxWatermarkLength"/> characters as people count them (text elements: a letter with its marks, an emoji).
    /// </summary>
    internal static WatermarkTextProblem? Check(string text, out string normal)
    {
        ArgumentNullException.ThrowIfNull(text);
        normal = "";
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                return WatermarkTextProblem.Invisible; // a lone surrogate: no character at all
        }

        // Before normalizing, which throws on U+FFFE and U+FFFF.
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsNoncharacter(rune.Value))
                return WatermarkTextProblem.Invisible;
        }

        normal = text.Normalize(NormalizationForm.FormC).Trim();
        if (normal.Length == 0)
            return WatermarkTextProblem.Empty;
        foreach (var rune in normal.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || rune.Value is 0x2028 or 0x2029)
                return WatermarkTextProblem.NotOneLine;
        }

        foreach (var rune in normal.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
                return WatermarkTextProblem.Invisible;
        }

        return new StringInfo(normal).LengthInTextElements > MaxWatermarkLength ? WatermarkTextProblem.TooLong : null;
    }

    // The 66 Unicode noncharacters: U+FDD0 to U+FDEF, and the last two code points of every plane (U+FFFE, U+1FFFF…).
    private static bool IsNoncharacter(int codePoint) => codePoint is >= 0xFDD0 and <= 0xFDEF || (codePoint & 0xFFFE) == 0xFFFE;
}

/// <summary>Why a text cannot be a watermark (<see cref="PdfEdit.Check"/>).</summary>
internal enum WatermarkTextProblem
{
    Empty,
    NotOneLine,
    Invisible,
    TooLong,
}
