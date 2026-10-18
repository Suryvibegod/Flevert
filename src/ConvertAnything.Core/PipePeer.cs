using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace ConvertAnything.Core;

/// <summary>Checks that the process at the other end of a batch pipe runs the same executable as this one.</summary>
internal static partial class PipePeer
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static uint ServerProcessId(NamedPipeClientStream client) =>
        GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var pid) ? pid : 0;

    public static uint ClientProcessId(NamedPipeServerStream server) =>
        GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out var pid) ? pid : 0;

    public static bool IsSameExecutable(uint processId)
    {
        if (processId == 0)
            return false;
        if (processId == Environment.ProcessId)
            return true;

        var other = ImagePath(processId);
        return other is not null && string.Equals(other, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ImagePath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
            return null;
        try
        {
            var buffer = new char[32768];
            var length = (uint)buffer.Length;
            return QueryFullProcessImageNameW(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(nint pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
