namespace Flevert.Core.Menu;

/// <summary>How an entry at the top of the Flevert menu looks.</summary>
/// <param name="Icon">A Fluent icon name, e.g. <c>arrow_swap</c>: the file <c>icons\arrow_swap.ico</c> beside flevert.exe.</param>
/// <param name="Group">Entries of one group go together; a separator goes between two groups (docs/specs/pdf.md, v0.2.2).</param>
public sealed record MenuEntryLook(string Icon, int Group);
