namespace Flevert.Core.Menu;

/// <summary>How an entry at the top of the Flevert menu looks.</summary>
/// <param name="Icon">A Fluent icon name, e.g. <c>arrow_swap</c>: the file <c>icons\arrow_swap.ico</c> beside flevert.exe.</param>
/// <param name="Group">Entries of one group go together; a separator goes between two groups (docs/specs/pdf.md, v0.2.2).</param>
public sealed record MenuEntryLook(string Icon, int Group)
{
    /// <summary>
    /// The looks of several modules in one dictionary, for <see cref="MenuInstaller.Install"/>, which finds an entry's look
    /// by its label in every menu. A label two modules share (Convert to, Compress, Remove hidden info) must look the same
    /// in both, or the menus of one file type would disagree with another's.
    /// </summary>
    /// <exception cref="ArgumentException">A label has two different looks.</exception>
    public static IReadOnlyDictionary<string, MenuEntryLook> Combine(params IEnumerable<IReadOnlyDictionary<string, MenuEntryLook>> looks)
    {
        ArgumentNullException.ThrowIfNull(looks);
        var combined = new Dictionary<string, MenuEntryLook>(StringComparer.Ordinal);
        foreach (var (label, look) in looks.SelectMany(set => set))
        {
            if (combined.TryGetValue(label, out var other) && other != look)
                throw new ArgumentException($"'{label}' looks like {other} in one menu and {look} in another.", nameof(looks));
            combined[label] = look;
        }

        return combined;
    }
}
