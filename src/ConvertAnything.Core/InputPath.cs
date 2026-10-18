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

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetLongPathNameW(string shortPath, [Out] char[] longPath, uint bufferLength);
}
