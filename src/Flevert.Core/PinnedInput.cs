using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Flevert.Core;

/// <summary>
/// The path an operation reads an input through. On Windows the runner's pin shares reading only, so the name stays on
/// the pinned file and the path is the name. Linux has no such lock: whoever can write the input's folder can point the
/// name at another file (a link to a private file, a FIFO) after the pin checked it, so a pinned input is read through the
/// pin itself, <c>/proc/&lt;flevert&gt;/fd/&lt;n&gt;</c>, which leads to the checked file whatever the name leads to now,
/// also from qpdf and the PDF worker (processes of the same user). The name stays what outputs are named after and what
/// messages show (docs/adr/0010-linux-nautilus-port.md).
/// </summary>
public static class PinnedInput
{
    // Set by the runner in the async method that runs the operation, so the pins flow into the operation and no further.
    private static readonly AsyncLocal<(string Name, string Handle)[]?> Pins = new();

    /// <summary>The path to read <paramref name="path"/> through: its pin's while the runner holds one, else the path itself.</summary>
    public static string ReadPath(string path)
    {
        foreach (var (name, handle) in Pins.Value ?? [])
        {
            if (string.Equals(name, path, StringComparison.Ordinal))
                return handle;
        }

        return path;
    }

    /// <summary>
    /// The length of <paramref name="path"/>, measured through its pin like its bytes are read: a <see cref="FileInfo"/> of
    /// the pin's path would describe the link /proc shows (64 bytes), not the file.
    /// </summary>
    public static long Length(string path)
    {
        var read = ReadPath(path);
        if (ReferenceEquals(read, path))
            return new FileInfo(path).Length;
        using var file = File.OpenHandle(read, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return RandomAccess.GetLength(file);
    }

    /// <summary><paramref name="text"/>, a tool's message, with every pinned input's handle path put back as its name.</summary>
    public static string ShowNames(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        // Longest first, so /proc/1/fd/7 is never replaced inside /proc/1/fd/70.
        foreach (var (name, handle) in (Pins.Value ?? []).OrderByDescending(pin => pin.Handle.Length))
            text = text.Replace(handle, name, StringComparison.Ordinal);
        return text;
    }

    /// <summary>Makes the operation run from the caller read each pinned input through its pin.</summary>
    [SupportedOSPlatform("linux")]
    internal static void Use(IEnumerable<(string Name, SafeFileHandle Pin)> pins) =>
        Pins.Value = [.. pins.Select(pin => (pin.Name, $"/proc/{Environment.ProcessId}/fd/{pin.Pin.DangerousGetHandle()}"))];
}
