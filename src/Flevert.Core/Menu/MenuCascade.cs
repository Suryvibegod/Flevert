namespace Flevert.Core.Menu;

/// <summary>
/// A top-level entry of the right-click menu that opens a cascade: Flevert itself, or Flevert PDF tools, the second one
/// PDFs have (docs/specs/pdf.md, v0.2.2). Explorer shows at most 16 entries of each cascade, so each has 16 of its own.
/// </summary>
/// <param name="Verb">Its key under <c>SystemFileAssociations\&lt;ext&gt;\shell</c>, which uninstall removes (also the
/// installer's safety net, installer\Flevert.iss).</param>
/// <param name="Title">What the menu shows.</param>
/// <param name="KeySuffix">Added to <c>Flevert.Menu&lt;ext&gt;</c> for the key that holds its entries.</param>
/// <param name="Icon">A Fluent icon name in the icons folder; <c>null</c> for flevert.exe's own icon.</param>
public sealed record MenuCascade(string Verb, string Title, string KeySuffix, string? Icon)
{
    public static MenuCascade Main { get; } = new(MenuInstaller.Title, MenuInstaller.Title, "", null);

    public static MenuCascade PdfTools { get; } = new(MenuInstaller.Title + ".Tools", "Flevert PDF tools", ".tools", "toolbox");

    /// <summary>Every cascade Flevert writes, in the order Explorer shows them (by key name).</summary>
    public static IReadOnlyList<MenuCascade> All { get; } = [Main, PdfTools];
}

/// <summary>An operation in a cascade of its own rather than under Flevert, such as the PDF tools.</summary>
public interface IMenuPlacement
{
    MenuCascade Cascade { get; }
}
