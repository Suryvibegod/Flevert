using System.Runtime.InteropServices;

namespace ConvertAnything.Core;

public static partial class InputPath
{
    private const string LongPrefix = @"\\?\";
    private const string LongUncPrefix = @"\\?\UNC\";

    /// <summary>
    /// Explorer passes 8.3 short names (e.g. <c>NHLT~1.TXT</c>) for paths longer than MAX_PATH; expand them so outputs
    /// are named after the real file. Returns the input unchanged when it cannot be expanded.
    /// </summary>
    public static string ToLongPath(string path)
    {
        if (!path.Contains('~', StringComparison.Ordinal))
            return path;

        var prefixed = path switch
        {
            _ when path.StartsWith(LongPrefix, StringComparison.Ordinal) => path,
            _ when path.StartsWith(@"\\", StringComparison.Ordinal) => LongUncPrefix + path[2..],
            _ => LongPrefix + path,
        };

        var buffer = new char[short.MaxValue];
        var length = GetLongPathNameW(prefixed, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length)
            return path;

        var expanded = new string(buffer, 0, (int)length);
        if (path.StartsWith(LongPrefix, StringComparison.Ordinal))
            return expanded;
        return expanded.StartsWith(LongUncPrefix, StringComparison.Ordinal)
            ? @"\\" + expanded[LongUncPrefix.Length..]
            : expanded[LongPrefix.Length..];
    }

    /// <summary>
    /// Whether any file or folder name in the path ends in a dot or a space. Win32 path normalisation drops those
    /// characters (dots from every name), so such a path names a different file once anything normalises it: a decoy
    /// <c>sub.\x.txt</c> would be read as <c>sub\x.txt</c>. The relative names <c>.</c> and <c>..</c> are fine.
    /// </summary>
    public static bool HasNameEndingInDotOrSpace(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase))
            p = p[LongUncPrefix.Length..];
        else if (p.StartsWith(LongPrefix, StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal))
            p = p[LongPrefix.Length..];

        foreach (var name in p.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (name is "." or ".." || name.Length == 0)
                continue;
            if (name[^1] is '.' or ' ')
                return true;
        }

        return false;
    }

    /// <summary>Whether the path names an NTFS alternate data stream (<c>a.txt:s.png</c>) rather than a file.</summary>
    public static bool HasStreamName(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(LongUncPrefix, StringComparison.OrdinalIgnoreCase))
            p = p[LongUncPrefix.Length..];
        else if (p.StartsWith(LongPrefix, StringComparison.Ordinal))
            p = p[LongPrefix.Length..];
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
            p = p[2..]; // the drive letter's colon
        return p.Contains(':', StringComparison.Ordinal);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);
}
