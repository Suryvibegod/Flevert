using System.Text;

namespace Flevert.Pdf.Tests;

/// <summary>
/// What any process of the same user can read about another on Linux, from /proc: the counterpart of the Windows reads of
/// <see cref="TestProcesses"/> and <see cref="ProcessPeek"/>. A process that ends while being read gives <c>null</c>.
/// </summary>
internal static class ProcFs
{
    public static int? ParentId(int processId) => Stat(processId) is { } fields && int.TryParse(fields[1], out var parent) ? parent : null;

    /// <summary>Running, not a zombie waiting to be collected.</summary>
    public static bool IsRunning(int processId) => Stat(processId) is { } fields && fields[0] is not ("Z" or "X");

    /// <summary>The arguments, the program first, joined by spaces.</summary>
    public static string? CommandLine(int processId) =>
        ReadBytes($"/proc/{processId}/cmdline") is { Length: > 0 } bytes
            ? string.Join(' ', Encoding.UTF8.GetString(bytes).TrimEnd('\0').Split('\0'))
            : null;

    /// <summary>The NAME=value entries, each ended by NUL, as the process was started with them.</summary>
    public static string? Environment(int processId) =>
        ReadBytes($"/proc/{processId}/environ") is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    // The fields after the command name, which is in parentheses and may hold spaces: state, parent, …
    private static string[]? Stat(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
