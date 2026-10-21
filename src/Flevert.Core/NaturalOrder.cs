using System.Runtime.InteropServices;

namespace Flevert.Core;

/// <summary>
/// The order the file manager shows file names in ("ảnh 2" before "ảnh 10"). On Windows Explorer's: Windows' own
/// StrCmpLogicalW, which Explorer sorts with (case ignored). On Linux Nautilus': <see cref="UnixNaturalOrder"/>. The
/// order in which Explorer starts one flevert.exe per file is not reliable (ADR-0002), so a batch
/// joined into one output is sorted with this (ADR-0007, part 2).
/// </summary>
/// <remarks>
/// Kept exactly as Windows has it, so the result matches what the user sees, including its limits: a run of digits too
/// large for 64 bits is compared as text, and the comparison follows the user's locale.
/// </remarks>
public static partial class NaturalOrder
{
    /// <summary>Compares file names, or any text, as Explorer does.</summary>
    public static IComparer<string> Comparer { get; } = Comparer<string>.Create(Compare);

    /// <summary>
    /// Compares paths by file name, then by the whole path, then ordinally, so files from several folders, or names that
    /// differ only in case, always come in one fixed order.
    /// </summary>
    public static IComparer<string> PathComparer { get; } = Comparer<string>.Create(ComparePaths);

    public static int Compare(string? x, string? y) =>
        x is null ? (y is null ? 0 : -1) : y is null ? 1 : CompareNames(x, y);

    // Nautilus is the file manager on Linux: its own order there (UnixNaturalOrder).
    private static int CompareNames(string x, string y) =>
        OperatingSystem.IsWindows() ? StrCmpLogicalW(x, y)
        : OperatingSystem.IsLinux() ? UnixNaturalOrder.Compare(x, y)
        : throw new PlatformNotSupportedException();

    /// <summary>Sorts paths with <see cref="PathComparer"/>.</summary>
    public static IReadOnlyList<string> SortPaths(IEnumerable<string> paths) => [.. paths.Order(PathComparer)];

    private static int ComparePaths(string? x, string? y)
    {
        var byName = Compare(Path.GetFileName(x), Path.GetFileName(y));
        if (byName != 0)
            return byName;
        var byPath = Compare(x, y);
        return byPath != 0 ? byPath : string.CompareOrdinal(x, y);
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string psz1, string psz2);
}
